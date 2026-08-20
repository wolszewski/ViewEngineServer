using Microsoft.Extensions.DependencyInjection;

namespace LiveViewEngine.TcpClient;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLiveViewEngineTcpIngestionClient(
        this IServiceCollection services,
        Action<LiveViewEngineTcpClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LiveViewEngineTcpClientOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<LiveViewEngineTcpClient>();
        services.AddSingleton<ILiveViewEngineTcpClient>(sp => sp.GetRequiredService<LiveViewEngineTcpClient>());
        services.AddHostedService<LiveViewEngineTcpClientHostedService>();
        return services;
    }
}
