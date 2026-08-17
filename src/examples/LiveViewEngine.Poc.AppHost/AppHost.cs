var builder = DistributedApplication.CreateBuilder(args);

var webHost = builder.AddProject<Projects.LiveViewEngine_WebHost>("webhost")
    .WithHttpEndpoint(port: 5100, name: "http", isProxied: false)
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.LiveViewEngine_Poc_DataProvider>("dataprovider")
    .WithReference(webHost)
    .WithEnvironment("WebHost__BaseUrl", "http://127.0.0.1:5100")
    .WithHttpEndpoint(port: 5101, name: "http", isProxied: false)
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.LiveViewEngine_Poc_Ui>("ui")
    .WithHttpEndpoint(port: 5102, name: "http", isProxied: false)
    .WithExternalHttpEndpoints();

builder.Build().Run();