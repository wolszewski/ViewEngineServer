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

    builder.AddProject<Projects.LiveViewEngine_Poc_DataProvider>("dataprovider")
        .WithEnvironment("WebHost__BaseUrl", "http://127.0.0.1:5100")
        .WithEnvironment("TcpIngest__Host", "127.0.0.1")
        .WithEnvironment("TcpIngest__Port", tcpIngestPort.ToString())
        .WithHttpEndpoint(port: 5101, name: "http", isProxied: false)
        .WithExternalHttpEndpoints();
}
else
{
    var webHost = builder.AddProject<Projects.LiveViewEngine_WebHost>("webhost")
        .WithEnvironment("TcpIngest__ListenAddress", "127.0.0.1")
        .WithEnvironment("TcpIngest__Port", tcpIngestPort.ToString())
        .WithHttpEndpoint(port: 5100, name: "http", isProxied: false)
        .WithExternalHttpEndpoints();

    builder.AddProject<Projects.LiveViewEngine_Poc_DataProvider>("dataprovider")
        .WithEnvironment("WebHost__BaseUrl", "http://127.0.0.1:5100")
        .WithEnvironment("TcpIngest__Host", "127.0.0.1")
        .WithEnvironment("TcpIngest__Port", tcpIngestPort.ToString())
        .WithHttpEndpoint(port: 5101, name: "http", isProxied: false)
        .WithExternalHttpEndpoints();
}

builder.AddProject<Projects.LiveViewEngine_Poc_Ui>("ui")
    .WithHttpEndpoint(port: 5102, name: "http", isProxied: false)
    .WithExternalHttpEndpoints();

builder.Build().Run();