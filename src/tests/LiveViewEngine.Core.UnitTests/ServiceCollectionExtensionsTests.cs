using Microsoft.Extensions.DependencyInjection;
using ViewEngineServer.WebApp.WebSocket;

namespace LiveViewEngine.Core.UnitTests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddLiveViewEnginePublisher_Generic_RegistersConcreteAndInterface()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLiveViewEngineCore(new LiveViewEngineOptions { EagerIndexing = false });
        services.AddSingleton(new WebSocketOutboundOptions());
        services.AddLiveViewEnginePublisher<WebSocketOutboundPublisher>();

        using var provider = services.BuildServiceProvider();
        var concrete = provider.GetRequiredService<WebSocketOutboundPublisher>();
        var outbound = provider.GetRequiredService<IOutboundPublisher>();
        var engine = provider.GetRequiredService<IViewEngine>();

        Assert.NotNull(engine);
        Assert.Same(concrete, outbound);
    }

    [Fact]
    public void AddLiveViewEnginePublisher_Factory_UsesProvidedInstance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLiveViewEngineCore(new LiveViewEngineOptions { EagerIndexing = false });
        var publisher = new TestPublisher();
        services.AddLiveViewEnginePublisher(_ => publisher);

        using var provider = services.BuildServiceProvider();
        var outbound = provider.GetRequiredService<IOutboundPublisher>();
        var engine = provider.GetRequiredService<IViewEngine>();

        Assert.NotNull(engine);
        Assert.Same(publisher, outbound);
    }

    [Fact]
    public void AddLiveViewEngineCore_DefaultOptions_SortingAndFilteringEnabledByDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddLiveViewEngineCore();

        Assert.True(builder.Options.SortingEnabled);
        Assert.True(builder.Options.FilteringEnabled);
    }

    [Fact]
    public void AddLiveViewEngineCore_RequireExplicitCapabilities_DisablesSortingAndFilteringUntilOptedIn()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddLiveViewEngineCore(new LiveViewEngineOptions { RequireExplicitCapabilities = true });

        Assert.False(builder.Options.SortingEnabled);
        Assert.False(builder.Options.FilteringEnabled);

        builder.AddSorting().AddFiltering();

        Assert.True(builder.Options.SortingEnabled);
        Assert.True(builder.Options.FilteringEnabled);
    }

    private sealed class TestPublisher : IOutboundPublisher
    {
        public ValueTask PublishAsync(
            IReadOnlyList<SubscriberTarget> targets,
            IReadOnlyList<ViewDelta> deltas,
            CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}
