using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.DataIngest;
using LiveViewEngine.Core.Output;
using LiveViewEngine.Core.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveViewEngine.Core.IntegrationTests;

public class ViewEngineCapabilityTests
{
    private static (ViewEngine engine, CapturingPublisher publisher) CreateEngine(LiveViewEngineOptions options)
    {
        var metrics = new ViewEngineMetrics();
        var store = new CollectionStore(metrics, options);
        var publisher = new CapturingPublisher();
        var engine = new ViewEngine(store, publisher, NullLogger<ViewEngine>.Instance, metrics);
        return (engine, publisher);
    }

    private static CollectionSchema TradesSchema() => new("trades", ["symbol", "price", "quantity"]);

    private static Task<IngestResult> CreateTrades(ViewEngine engine) =>
        engine.IngestAsync(new CreateCollectionCommand
        {
            CollectionId = "trades",
            Schema = TradesSchema()
        });

    [Fact]
    public async Task DefaultOptions_AllowsSortColumnAndFilters()
    {
        var (engine, _) = CreateEngine(new LiveViewEngineOptions { EagerIndexing = false });
        await CreateTrades(engine);

        var result = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition
            {
                CollectionId = "trades",
                SortColumn = "price",
                Filters = [new FilterSpec("symbol", FilterOperator.Eq, "AAPL")]
            },
            StartIndex = 0,
            PageSize = 200
        });

        Assert.NotEmpty(result);
        Assert.IsNotType<SubscriptionRejectedDelta>(result[0]);
    }

    [Fact]
    public async Task RequireExplicitCapabilities_RejectsSortColumn_WhenSortingNotEnabled()
    {
        var (engine, _) = CreateEngine(new LiveViewEngineOptions
        {
            EagerIndexing = false,
            RequireExplicitCapabilities = true,
            SortingEnabled = false,
            FilteringEnabled = false
        });
        await CreateTrades(engine);

        var result = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition { CollectionId = "trades", SortColumn = "price" },
            StartIndex = 0,
            PageSize = 200
        });

        var rejected = Assert.IsType<SubscriptionRejectedDelta>(Assert.Single(result));
        Assert.Equal("sorting_not_enabled", rejected.Reason);
    }

    [Fact]
    public async Task RequireExplicitCapabilities_RejectsFilters_WhenFilteringNotEnabled()
    {
        var (engine, _) = CreateEngine(new LiveViewEngineOptions
        {
            EagerIndexing = false,
            RequireExplicitCapabilities = true,
            SortingEnabled = false,
            FilteringEnabled = false
        });
        await CreateTrades(engine);

        var result = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition
            {
                CollectionId = "trades",
                Filters = [new FilterSpec("symbol", FilterOperator.Eq, "AAPL")]
            },
            StartIndex = 0,
            PageSize = 200
        });

        var rejected = Assert.IsType<SubscriptionRejectedDelta>(Assert.Single(result));
        Assert.Equal("filtering_not_enabled", rejected.Reason);
    }

    [Fact]
    public async Task RequireExplicitCapabilities_AllowsPlainSubscribe_WithNoSortOrFilters()
    {
        var (engine, _) = CreateEngine(new LiveViewEngineOptions
        {
            EagerIndexing = false,
            RequireExplicitCapabilities = true,
            SortingEnabled = false,
            FilteringEnabled = false
        });
        await CreateTrades(engine);

        var result = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition { CollectionId = "trades" },
            StartIndex = 0,
            PageSize = 200
        });

        Assert.NotEmpty(result);
        Assert.IsNotType<SubscriptionRejectedDelta>(result[0]);
    }

    [Fact]
    public async Task RequireExplicitCapabilities_RejectsUpdateViewCommand_RequestingSortColumn()
    {
        var (engine, _) = CreateEngine(new LiveViewEngineOptions
        {
            EagerIndexing = false,
            RequireExplicitCapabilities = true,
            SortingEnabled = false,
            FilteringEnabled = false
        });
        await CreateTrades(engine);

        var subscribeResult = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            View = new ViewDefinition { CollectionId = "trades" },
            StartIndex = 0,
            PageSize = 200
        });
        Assert.IsNotType<SubscriptionRejectedDelta>(subscribeResult[0]);

        var updateResult = await engine.SubscribeAsync(new UpdateViewCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            SortColumn = "price"
        });

        var rejected = Assert.IsType<SubscriptionRejectedDelta>(Assert.Single(updateResult));
        Assert.Equal("sorting_not_enabled", rejected.Reason);
    }

    [Fact]
    public async Task RequireExplicitCapabilities_RejectsFilterPreset_WhenFilteringNotEnabled()
    {
        var (engine, _) = CreateEngine(new LiveViewEngineOptions
        {
            EagerIndexing = false,
            RequireExplicitCapabilities = true,
            SortingEnabled = false,
            FilteringEnabled = false
        });
        await CreateTrades(engine);

        var presetResult = await engine.IngestAsync(new CreateFilterPresetCommand
        {
            CollectionId = "trades",
            FilterPresetId = "aaplOnly",
            Filters = [new FilterSpec("symbol", FilterOperator.Eq, "AAPL")]
        });
        Assert.True(presetResult.Success, presetResult.Error);

        // A subscribe carrying only a FilterPresetId (no explicit View.Filters) must still be
        // rejected: CollectionRuntime.ResolveViewKey expands the preset into real server-side
        // filters, so the effective filter set is non-empty even though View.Filters is [].
        var result = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition { CollectionId = "trades", FilterPresetId = "aaplOnly" },
            StartIndex = 0,
            PageSize = 200
        });

        var rejected2 = Assert.IsType<SubscriptionRejectedDelta>(Assert.Single(result));
        Assert.Equal("filtering_not_enabled", rejected2.Reason);
    }
}
