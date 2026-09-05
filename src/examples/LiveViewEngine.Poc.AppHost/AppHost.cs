var builder = DistributedApplication.CreateBuilder(args);

var useWebHostContainerArg = args.FirstOrDefault(arg =>
    arg.StartsWith("--use-webhost-container", StringComparison.OrdinalIgnoreCase)
    || arg.StartsWith("--UseWebHostInContainer", StringComparison.OrdinalIgnoreCase));

var useWebHostContainer =
    bool.TryParse(builder.Configuration["UseWebHostInContainer"], out var configuredValue) && configuredValue
    || useWebHostContainerArg is not null &&
        !useWebHostContainerArg.Contains("false", StringComparison.OrdinalIgnoreCase);
var tcpIngestPort =
    int.TryParse(builder.Configuration["TcpIngestPort"], out var configuredTcpIngestPort)
        ? configuredTcpIngestPort
        : 6000;
var includeLightstreamer = bool.TryParse(builder.Configuration["IncludeLightstreamer"], out var configuredIncludeLightstreamer)
    && configuredIncludeLightstreamer;

if (useWebHostContainer)
{
    builder.AddDockerfile("webhost", "../../..", "src/LiveViewEngine.WebHost/Dockerfile")
        .WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
        .WithEnvironment("TcpIngest__ListenAddress", "0.0.0.0")
        .WithEnvironment("TcpIngest__Port", tcpIngestPort.ToString())
        .WithHttpEndpoint(port: 5100, targetPort: 8080, isProxied: false)
        .WithEndpoint(port: tcpIngestPort, targetPort: tcpIngestPort, scheme: "tcp", name: "tcp-ingest", isProxied: false)
        .WithExternalHttpEndpoints()
        .WithContainerRuntimeArgs("--cpus=4", "--memory=8g");
}
else
{
    builder.AddProject<Projects.LiveViewEngine_WebHost>("webhost")
        .WithEnvironment("TcpIngest__ListenAddress", "127.0.0.1")
        .WithEnvironment("TcpIngest__Port", tcpIngestPort.ToString())
        .WithHttpEndpoint(port: 5100, name: "http", isProxied: false)
        .WithExternalHttpEndpoints();
}

builder.AddProject<Projects.LiveViewEngine_Poc_DataProvider>("dataprovider")
    .WithEnvironment("WebHost__BaseUrl", "http://127.0.0.1:5100")
    .WithEnvironment("TcpIngest__Host", "127.0.0.1")
    .WithEnvironment("TcpIngest__Port", tcpIngestPort.ToString())
    .WithHttpEndpoint(port: 5101, name: "http", isProxied: false)
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.LiveViewEngine_Poc_Ui>("ui")
    .WithHttpEndpoint(port: 5102, name: "http", isProxied: false)
    .WithExternalHttpEndpoints();

if (includeLightstreamer)
{
    var lightstreamer = builder.AddContainer("lightstreamer", "lightstreamer", "latest")
        .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http")
        .WithEndpoint(port: 6661, targetPort: 6661, name: "request-reply")
        .WithEndpoint(port: 6662, targetPort: 6662, name: "request-reply-command")
        .WithBindMount(Path.GetFullPath("./lightstreamer-adapters"), "/lightstreamer/adapters/TRADES")
        .WithContainerRuntimeArgs("--cpus=4", "--memory=8g")
        .WithLifetime(ContainerLifetime.Session);

    var lightstreamerDataProvider = builder.AddProject<Projects.Lightstreamer_DataProvider>("lightstreamer-dataprovider")
        .WithHttpEndpoint(port: 5111, name: "http", isProxied: false)
        .WithExternalHttpEndpoints()
        .WaitFor(lightstreamer);

    builder.AddProject<Projects.Lightstreamer_UI>("lightstreamer-ui")
        .WithEnvironment("Lightstreamer__BaseUrl", "http://127.0.0.1:8080")
        .WithHttpEndpoint(port: 5112, name: "http", isProxied: false)
        .WithExternalHttpEndpoints()
        .WaitFor(lightstreamer)
        .WaitFor(lightstreamerDataProvider);
}

builder.Build().Run();