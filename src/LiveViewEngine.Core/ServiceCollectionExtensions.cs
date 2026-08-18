using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Output;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LiveViewEngine.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLiveViewEngineCore(this IServiceCollection services, LiveViewEngineOptions? options = null)
    {
        services.AddSingleton<IViewEngineMetrics, ViewEngineMetrics>();
        services.AddSingleton<ICollectionStore, CollectionStore>();
        services.AddSingleton<IOutboundEventFormatter, JsonOutboundEventFormatter>();
        services.AddSingleton(options ?? new LiveViewEngineOptions());
        services.AddSingleton<IViewEngine, ViewEngine>();
        services.AddHostedService<StaleIndexReaperService>();
        return services;
    }

    public static IServiceCollection AddLiveViewEnginePublisher<TPublisher>(this IServiceCollection services)
        where TPublisher : class, IOutboundPublisher
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<TPublisher>();
        services.AddSingleton<IOutboundPublisher>(sp => sp.GetRequiredService<TPublisher>());
        return services;
    }

    public static IServiceCollection AddLiveViewEnginePublisher(
        this IServiceCollection services,
        Func<IServiceProvider, IOutboundPublisher> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);

        services.AddSingleton(factory);
        return services;
    }
}