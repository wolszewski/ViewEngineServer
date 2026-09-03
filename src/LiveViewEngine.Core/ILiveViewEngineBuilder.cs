using Microsoft.Extensions.DependencyInjection;

namespace LiveViewEngine.Core;

// Thin wrapper over IServiceCollection returned by AddLiveViewEngineCore(), so downstream
// packages (e.g. a future LiveViewEngine.SortFilter) can add builder-scoped extension methods
// (.AddSorting()/.AddFiltering()) without depending on each other. Mirrors the
// AddIdentity().AddEntityFrameworkStores()-style builder pattern.
public interface ILiveViewEngineBuilder
{
    IServiceCollection Services { get; }

    // The LiveViewEngineOptions instance registered by AddLiveViewEngineCore. Builder-scoped
    // extension methods mutate SortingEnabled/FilteringEnabled/RowProjector on this shared
    // instance rather than re-registering a new options object.
    LiveViewEngineOptions Options { get; }
}

internal sealed class LiveViewEngineBuilder(IServiceCollection services, LiveViewEngineOptions options) : ILiveViewEngineBuilder
{
    public IServiceCollection Services { get; } = services;
    public LiveViewEngineOptions Options { get; } = options;
}
