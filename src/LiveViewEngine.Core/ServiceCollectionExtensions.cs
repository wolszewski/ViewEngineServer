using LiveViewEngine.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveViewEngine.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLiveViewEngineCore(this IServiceCollection services)
    {
        services.AddSingleton<ICollectionStore, CollectionStore>();
        services.AddSingleton<IViewEngine, ViewEngine>();
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