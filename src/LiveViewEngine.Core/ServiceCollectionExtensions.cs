using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Output;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LiveViewEngine.Core;

public static class ServiceCollectionExtensions
{
    public static ILiveViewEngineBuilder AddLiveViewEngineCore(this IServiceCollection services, LiveViewEngineOptions? options = null)
    {
        // RequireExplicitCapabilities' effect on SortingEnabled/FilteringEnabled's defaults is
        // enforced directly by LiveViewEngineOptions itself (see its property getters), so it holds
        // regardless of construction path - no extra handling needed here.
        var resolvedOptions = options ?? new LiveViewEngineOptions();

        services.AddSingleton<IViewEngineMetrics, ViewEngineMetrics>();
        services.AddSingleton<ICollectionStore, CollectionStore>();
        services.AddSingleton<IOutboundEventFormatter, JsonOutboundEventFormatter>();
        services.AddSingleton(resolvedOptions);
        services.AddSingleton<IViewEngine, ViewEngine>();
        services.AddHostedService<StaleIndexReaperService>();
        return new LiveViewEngineBuilder(services, resolvedOptions);
    }

    // TODO(plugin-assembly-split): move AddSorting()/AddFiltering() to a separate
    // LiveViewEngine.SortFilter project once SortIndex/FilterSet are physically extracted from
    // Core (see plan.md Phase 2). For now they just flip the capability flags checked by
    // ViewEngine's subscribe-time rejection — the real SortIndex/FilterSet code always ships with
    // Core, so this is a DI-level opt-in only, not yet a physically omittable assembly.
    public static ILiveViewEngineBuilder AddSorting(this ILiveViewEngineBuilder builder)
    {
        builder.Options.SortingEnabled = true;
        return builder;
    }

    public static ILiveViewEngineBuilder AddFiltering(this ILiveViewEngineBuilder builder)
    {
        builder.Options.FilteringEnabled = true;
        return builder;
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