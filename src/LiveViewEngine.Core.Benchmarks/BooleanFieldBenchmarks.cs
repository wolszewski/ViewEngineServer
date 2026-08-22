using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.DataIngest;
using LiveViewEngine.Core.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveViewEngine.Core.Benchmarks;

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "RatioSD")]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class BooleanFieldBenchmarks
{
    private const string CollectionId = "boolean_objects";

    [Params(200_000)]
    public int RowCount;

    private UpsertRowCommand[] _seedCommands = [];
    private UpsertRowCommand[] _updateCommands = [];
    private ViewEngine _booleanSchemaUpdateEngine = null!;
    private ViewEngine _stringUpdateEngine = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _seedCommands = BuildSeedCommands(RowCount);
        _updateCommands = BuildUpdateCommands(RowCount);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _booleanSchemaUpdateEngine = BuildAndPopulateEngine(useBooleanSchema: true);
        _stringUpdateEngine = BuildAndPopulateEngine(useBooleanSchema: false);

        var updateView = new ViewDefinition
        {
            CollectionId = CollectionId,
            SortColumn = "active",
            SortAscending = true,
            Filters = [new FilterSpec("active", FilterOperator.Eq, "true")]
        };

        Subscribe(_booleanSchemaUpdateEngine, updateView);
        Subscribe(_stringUpdateEngine, updateView);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Snapshot_FilteredSorted")]
    public Task StringBool_Snapshot_FilteredSorted()
    {
        var engine = BuildAndPopulateEngine(useBooleanSchema: false);
        return Subscribe(
            engine,
            new ViewDefinition
            {
                CollectionId = CollectionId,
                SortColumn = "active",
                SortAscending = true,
                Filters = [new FilterSpec("active", FilterOperator.Eq, "true")]
            });
    }

    [Benchmark]
    [BenchmarkCategory("Snapshot_FilteredSorted")]
    public Task BooleanSchema_Snapshot_FilteredSorted()
    {
        var engine = BuildAndPopulateEngine(useBooleanSchema: true);
        return Subscribe(
            engine,
            new ViewDefinition
            {
                CollectionId = CollectionId,
                SortColumn = "active",
                SortAscending = true,
                Filters = [new FilterSpec("active", FilterOperator.Eq, "true")]
            });
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Snapshot_SortedOnly")]
    public Task StringBool_Snapshot_SortedOnly()
    {
        var engine = BuildAndPopulateEngine(useBooleanSchema: false);
        return Subscribe(
            engine,
            new ViewDefinition
            {
                CollectionId = CollectionId,
                SortColumn = "active",
                SortAscending = true
            });
    }

    [Benchmark]
    [BenchmarkCategory("Snapshot_SortedOnly")]
    public Task BooleanSchema_Snapshot_SortedOnly()
    {
        var engine = BuildAndPopulateEngine(useBooleanSchema: true);
        return Subscribe(
            engine,
            new ViewDefinition
            {
                CollectionId = CollectionId,
                SortColumn = "active",
                SortAscending = true
            });
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Ingest_UpdatePropagation")]
    public async Task StringBool_IngestUpdates_WithFilteredSortedSubscriber()
    {
        foreach (var command in _updateCommands)
        {
            await _stringUpdateEngine.IngestAsync(command);
        }
    }

    [Benchmark]
    [BenchmarkCategory("Ingest_UpdatePropagation")]
    public async Task BooleanSchema_IngestUpdates_WithFilteredSortedSubscriber()
    {
        foreach (var command in _updateCommands)
        {
            await _booleanSchemaUpdateEngine.IngestAsync(command);
        }
    }

    private ViewEngine BuildAndPopulateEngine(bool useBooleanSchema)
    {
        var engine = BuildEngine(useBooleanSchema);
        foreach (var command in _seedCommands)
        {
            engine.IngestAsync(command).GetAwaiter().GetResult();
        }

        return engine;
    }

    private static ViewEngine BuildEngine(bool useBooleanSchema)
    {
        var fieldNames = new[] { "id", "active", "payload" };
        var fieldTypes = new[]
        {
            ScalarFieldType.String,
            useBooleanSchema ? ScalarFieldType.Boolean : ScalarFieldType.String,
            ScalarFieldType.String
        };

        var schema = new CollectionSchema(CollectionId, fieldNames, fieldTypes);
        var store = new CollectionStore(null);
        var engine = new ViewEngine(store, new NullPublisher(), NullLogger<ViewEngine>.Instance, null);
        engine.IngestAsync(new CreateCollectionCommand { CollectionId = CollectionId, Schema = schema })
            .GetAwaiter()
            .GetResult();

        return engine;
    }

    private static Task Subscribe(ViewEngine engine, ViewDefinition view)
    {
        return engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            View = view,
            StartIndex = 0,
            PageSize = 200
        });
    }

    private static UpsertRowCommand[] BuildSeedCommands(int rowCount)
    {
        var commands = new UpsertRowCommand[rowCount];
        for (var i = 0; i < rowCount; i++)
        {
            var isActive = i % 2 == 0 ? "true" : "false";
            commands[i] = new UpsertRowCommand
            {
                CollectionId = CollectionId,
                Key = $"R{i + 1:D5}",
                Fields = new Dictionary<string, string?>
                {
                    ["id"] = $"R{i + 1:D5}",
                    ["active"] = isActive,
                    ["payload"] = $"p-{i % 250}"
                }
            };
        }

        return commands;
    }

    private static UpsertRowCommand[] BuildUpdateCommands(int rowCount)
    {
        var commands = new UpsertRowCommand[rowCount];
        for (var i = 0; i < rowCount; i++)
        {
            var toggledActive = i % 2 == 0 ? "false" : "true";
            commands[i] = new UpsertRowCommand
            {
                CollectionId = CollectionId,
                Key = $"R{i + 1:D5}",
                Fields = new Dictionary<string, string?>
                {
                    ["active"] = toggledActive,
                    ["payload"] = $"u-{i % 250}"
                }
            };
        }

        return commands;
    }

    private sealed class NullPublisher : IOutboundPublisher
    {
        public ValueTask PublishAsync(
            IReadOnlyList<SubscriberTarget> targets,
            IReadOnlyList<ViewDelta> deltas,
            CancellationToken ct = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken ct = default)
        {
            return ValueTask.CompletedTask;
        }
    }
}
