using LiveViewEngine.HttpClient;
using LiveViewEngine.TcpClient;
using LiveViewEngine.WebHost.LoadTests.Config;
using LiveViewEngine.WebHost.LoadTests.Ingest;
using LiveViewEngine.WebHost.LoadTests.Scenarios;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Sinks.Timescale;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var settings = new LoadTestSettings();
configuration.GetSection("LoadTest").Bind(settings);
var nbomberArgs = CliArgs.ParseAndApply(args, settings);

using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

using var tcpClientCts = new CancellationTokenSource();
Task? tcpRunLoop = null;
IIngestClient ingestClient;

if (settings.IngestMode == IngestMode.Tcp)
{
    var tcpOptions = new LiveViewEngineTcpClientOptions { Host = settings.TcpHost, Port = settings.TcpPort };
    var tcpClient = new LiveViewEngineTcpClient(tcpOptions, loggerFactory.CreateLogger<LiveViewEngineTcpClient>());
    tcpRunLoop = tcpClient.RunAsync(tcpClientCts.Token);
    ingestClient = new TcpIngestClient(tcpClient);
}
else
{
    var httpClient = new System.Net.Http.HttpClient { BaseAddress = new Uri(settings.BaseHttpUrl) };
    var lveHttpClient = new LiveViewEngineHttpClient(httpClient, loggerFactory.CreateLogger<LiveViewEngineHttpClient>());
    ingestClient = new HttpIngestClient(lveHttpClient);
}

var scenarios = new List<ScenarioProps>();
if (settings.Scenario is LoadTestScenario.All or LoadTestScenario.Subscribe)
{
    // subscribe_latency measures loading a snapshot, so the collection must be pre-seeded with
    // rows up-front (before the scenario runs) rather than left empty.
    var created = await ingestClient.CreateCollectionAsync(settings.CollectionName, ["symbol", "updatedAt"], ["string", "string"]);
    if (!created)
    {
        throw new InvalidOperationException($"subscribe_latency: could not create collection '{settings.CollectionName}'.");
    }

    await SnapshotSeeder.SeedAsync(settings, ingestClient, loggerFactory.CreateLogger("SnapshotSeeder"));
    scenarios.Add(SubscribeLatencyScenario.Create(settings));
}

if (settings.Scenario is LoadTestScenario.All or LoadTestScenario.Delta)
{
    scenarios.Add(DeltaLatencyScenario.Create(settings, ingestClient));
}

var nbomberContext = NBomberRunner
    .RegisterScenarios(scenarios.ToArray())
    .WithTestSuite("LiveViewEngine.WebHost")
    .WithTestName("webhost-ws-load-test")
    .WithReportFormats(NBomber.Contracts.Stats.ReportFormat.Html, NBomber.Contracts.Stats.ReportFormat.Csv);

if (settings.EnableStudio)
{
    var sinkConfig = new TimescaleDbSinkConfig { ConnectionString = settings.TimescaleConnectionString };
    nbomberContext = nbomberContext.WithReportingSinks(new TimescaleDbSink(sinkConfig));
}

var stats = nbomberContext.Run(nbomberArgs);

tcpClientCts.Cancel();
if (tcpRunLoop is not null)
{
    try
    {
        await tcpRunLoop;
    }
    catch (OperationCanceledException) { /* expected shutdown */ }
}
