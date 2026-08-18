var builder = DistributedApplication.CreateBuilder(args);

var lightstreamer = builder.AddContainer("lightstreamer", "lightstreamer", "latest")
    .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http")
    .WithEndpoint(port: 6661, targetPort: 6661, name: "request-reply")
    .WithEndpoint(port: 6662, targetPort: 6662, name: "request-reply-command")
    .WithBindMount(Path.GetFullPath("./lightstreamer-adapters"), "/lightstreamer/adapters/TRADES")
    .WithContainerRuntimeArgs("--cpus=4", "--memory=8g")
    .WithLifetime(ContainerLifetime.Session);

var dataProvider = builder.AddProject<Projects.Lightstreamer_DataProvider>("lightstreamer-dataprovider")
    .WithHttpEndpoint(port: 5101, name: "http", isProxied: false)
    .WithExternalHttpEndpoints()
    .WaitFor(lightstreamer);

builder.AddProject<Projects.Lightstreamer_UI>("lightstreamer-ui")
    .WithEnvironment("Lightstreamer__BaseUrl", "http://127.0.0.1:8080")
    .WithHttpEndpoint(port: 5102, name: "http", isProxied: false)
    .WithExternalHttpEndpoints()
    .WaitFor(lightstreamer)
    .WaitFor(dataProvider);

builder.Build().Run();
