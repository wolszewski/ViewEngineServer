using Microsoft.Extensions.Hosting;

namespace LiveViewEngine.TcpClient;

internal sealed class LiveViewEngineTcpClientHostedService(
    LiveViewEngineTcpClient client) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => client.RunAsync(stoppingToken);
}
