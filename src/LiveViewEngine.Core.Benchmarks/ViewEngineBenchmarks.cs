using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using BenchmarkDotNet.Attributes;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.DataIngest;
using LiveViewEngine.Core.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveViewEngine.Core.Benchmarks;

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "RatioSD")]
public class ViewEngineBenchmarks
{
    private const int N = 10000;
    private const string CollectionId = "objects";
    private static readonly string[] Categories = ["category1", "category2", "category3", "category4"];

    private UpsertRowCommand[] _commands = [];

    [GlobalSetup]
    public void Setup()
    {
        _commands = BuildInsertCommands(N);
    }

    internal static UpsertRowCommand[] BuildInsertCommands(int n)
    {
        var commands = new UpsertRowCommand[n];
        for (int i = 0; i < n; i++)
        {
            commands[i] = new UpsertRowCommand
            {
                CollectionId = CollectionId,
                Key = $"O{i + 1:D5}",
                Fields = new Dictionary<string, string?>
                {
                    ["id"] = $"O{i + 1:D5}",
                    ["date"] = $"2024-{(i % 12 + 1):D2}-{(i % 28 + 1):D2}",
                    ["category"] = Categories[i % 4],
                    ["f01"] = $"v{i % 100}",
                    ["f02"] = i % 2 == 0 ? "A" : "B",
                    ["f03"] = $"{(i % 1000) * 100}",
                    ["f04"] = $"{99 + (i % 50):F2}",
                    ["f05"] = "USD",
                    ["f06"] = $"TRADER-{i % 10}",
                    ["f07"] = $"CP-{i % 20}",
                    ["f08"] = $"BOOK-{i % 5}",
                    ["f09"] = $"PORT-{i % 8}",
                    ["f10"] = "IRSwap",
                    ["f11"] = $"2026-{(i % 12 + 1):D2}-01",
                    ["f12"] = $"2024-{(i % 12 + 1):D2}-{((i % 28 + 2) % 28 + 1):D2}",
                    ["f13"] = "LME",
                    ["f14"] = $"STRAT-{i % 3}",
                    ["f15"] = $"note-{i}",
                    ["f16"] = $"ref-{i % 500}",
                    ["f17"] = ""
                }
            };
        }
        return commands;
    }

    [Benchmark] public async Task Insert10k_NoSubscribers()
    {
        var engine = CreateEngine([]);
        foreach (var command in _commands) { await engine.IngestAsync(command); }
    }

    [Benchmark] public async Task Insert10k_Unfiltered_1Subscriber()
    {
        var engine = CreateEngine([new ViewDefinition { CollectionId = CollectionId }]);
        foreach (var command in _commands) { await engine.IngestAsync(command); }
    }

    [Benchmark] public async Task Insert10k_Unfiltered_2Subscribers()
    {
        var view = new ViewDefinition { CollectionId = CollectionId };
        var engine = CreateEngine([view, view]);
        foreach (var command in _commands) { await engine.IngestAsync(command); }
    }

    [Benchmark] public async Task Insert10k_Unfiltered_10Subscribers()
    {
        var view = new ViewDefinition { CollectionId = CollectionId };
        var engine = CreateEngine(Enumerable.Repeat(view, 10).ToArray());
        foreach (var command in _commands) { await engine.IngestAsync(command); }
    }

    [Benchmark] public async Task Insert10k_SortedOnly_1Subscriber()
    {
        var view = new ViewDefinition { CollectionId = CollectionId, SortColumn = "date", SortAscending = false };
        var engine = CreateEngine([view]);
        foreach (var command in _commands) { await engine.IngestAsync(command); }
    }

    [Benchmark] public async Task Insert10k_SortedOnly_2Subscribers()
    {
        var view = new ViewDefinition { CollectionId = CollectionId, SortColumn = "date", SortAscending = false };
        var engine = CreateEngine([view, view]);
        foreach (var command in _commands) { await engine.IngestAsync(command); }
    }

    [Benchmark] public async Task Insert10k_SortedOnly_10Subscribers()
    {
        var view = new ViewDefinition { CollectionId = CollectionId, SortColumn = "date", SortAscending = false };
        var engine = CreateEngine(Enumerable.Repeat(view, 10).ToArray());
        foreach (var command in _commands) { await engine.IngestAsync(command); }
    }

    [Benchmark] public async Task Insert10k_SortedAndFiltered_1Subscriber()
    {
        var view = new ViewDefinition
        {
            CollectionId = CollectionId, SortColumn = "date", SortAscending = false,
            Filters = [new FilterSpec("category", FilterOperator.Eq, "category1")]
        };
        var engine = CreateEngine([view]);
        foreach (var command in _commands) { await engine.IngestAsync(command); }
    }

    [Benchmark] public async Task Insert10k_SortedAndFiltered_2Subscribers()
    {
        var view = new ViewDefinition
        {
            CollectionId = CollectionId, SortColumn = "date", SortAscending = false,
            Filters = [new FilterSpec("category", FilterOperator.Eq, "category1")]
        };
        var engine = CreateEngine([view, view]);
        foreach (var command in _commands) { await engine.IngestAsync(command); }
    }

    [Benchmark] public async Task Insert10k_SortedAndFiltered_10Subscribers()
    {
        var view = new ViewDefinition
        {
            CollectionId = CollectionId, SortColumn = "date", SortAscending = false,
            Filters = [new FilterSpec("category", FilterOperator.Eq, "category1")]
        };
        var engine = CreateEngine(Enumerable.Repeat(view, 10).ToArray());
        foreach (var command in _commands) { await engine.IngestAsync(command); }
    }

    [Benchmark] public async Task Insert10k_DifferentViews_2Subscribers()
    {
        var engine = CreateEngine(BuildDistinctViews(2));
        foreach (var command in _commands) { await engine.IngestAsync(command); }
    }

    [Benchmark] public async Task Insert10k_DifferentViews_10Subscribers()
    {
        var engine = CreateEngine(BuildDistinctViews(10));
        foreach (var command in _commands) { await engine.IngestAsync(command); }
    }

    [Benchmark] public async Task Insert10k_SameSort_DifferentFilters_4Subscribers()
    {
        var engine = CreateEngine(BuildSameSortDifferentFilterViews(4));
        foreach (var command in _commands) { await engine.IngestAsync(command); }
    }

    // Generates N unique ViewDefinitions with distinct sort columns and filters.
    internal static ViewDefinition[] BuildDistinctViews(int count)
    {
        string[] sortColumns = ["f01", "f02", "f03", "f04", "f05", "f06", "f07", "f08", "f09", "f10"];
        string[] filterCategories = ["category1", "category2", "category3", "category4"];
        var views = new ViewDefinition[count];
        for (int i = 0; i < count; i++)
        {
            views[i] = new ViewDefinition
            {
                CollectionId = CollectionId,
                SortColumn = sortColumns[i % sortColumns.Length],
                SortAscending = i % 2 == 0,
                Filters = [new FilterSpec("category", FilterOperator.Eq, filterCategories[i % filterCategories.Length])]
            };
        }
        return views;
    }

    internal static ViewDefinition[] BuildSameSortDifferentFilterViews(int count)
    {
        string[] categories = ["category1", "category2", "category3", "category4"];
        var views = new ViewDefinition[count];
        for (int i = 0; i < count; i++)
        {
            views[i] = new ViewDefinition
            {
                CollectionId = CollectionId,
                SortColumn = "date",
                SortAscending = false,
                Filters = [new FilterSpec("category", FilterOperator.Eq, categories[i % categories.Length])]
            };
        }
        return views;
    }

    internal static ViewEngine CreateEngine(IReadOnlyList<ViewDefinition> views)
    {
        var schema = new CollectionSchema(CollectionId, [
            "id", "date", "category", "f01", "f02", "f03", "f04", "f05",
            "f06", "f07", "f08", "f09", "f10", "f11", "f12", "f13",
            "f14", "f15", "f16", "f17"
        ]);
        var store = new CollectionStore(null);
        var engine = new ViewEngine(store, new NullPublisher(), NullLogger<ViewEngine>.Instance, null);

        engine.IngestAsync(new CreateCollectionCommand { CollectionId = CollectionId, Schema = schema })
              .GetAwaiter().GetResult();

        for (int i = 0; i < views.Count; i++)
        {
            engine.SubscribeAsync(new SubscribeCommand
            {
                ConnectionId = i + 1,
                View = views[i],
                StartIndex = 0,
                PageSize = 50
            }).GetAwaiter().GetResult();
        }

        return engine;
    }

    internal sealed class NullPublisher : IOutboundPublisher
    {
        public ValueTask PublishAsync(
            IReadOnlyList<SubscriberTarget> targets,
            IReadOnlyList<ViewDelta> deltas,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "RatioSD")]
public class ViewEngineModifyBenchmarks
{
    private const int N = 10000;
    private const string CollectionId = "objects";
    private static readonly string[] ModifiableFields =
        ["f01", "f02", "f03", "f04", "f05", "f06", "f07", "f08", "f09", "f10", "f11", "f12", "f13", "f14", "f15", "f16", "f17"];

    private UpsertRowCommand[] _modifyCommands = [];

    // Pre-populated engines per variant — recreated each iteration so mutations don't accumulate.
    private ViewEngine _engineNoSub = null!;
    private ViewEngine _engineUnfiltered1 = null!;
    private ViewEngine _engineUnfiltered2 = null!;
    private ViewEngine _engineUnfiltered10 = null!;
    private ViewEngine _engineSorted1 = null!;
    private ViewEngine _engineSorted2 = null!;
    private ViewEngine _engineSorted10 = null!;
    private ViewEngine _engineFiltered1 = null!;
    private ViewEngine _engineFiltered2 = null!;
    private ViewEngine _engineFiltered10 = null!;
    private ViewEngine _engineSameSort4 = null!;
    private ViewEngine _engineDiff2 = null!;
    private ViewEngine _engineDiff10 = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var rng = new Random(42);
        _modifyCommands = new UpsertRowCommand[N];
        for (int i = 0; i < N; i++)
        {
            int fieldCount = 1 + rng.Next(5);
            var arr = (string[])ModifiableFields.Clone();
            for (int k = 0; k < fieldCount; k++)
            {
                int j = k + rng.Next(ModifiableFields.Length - k);
                (arr[k], arr[j]) = (arr[j], arr[k]);
            }

            var fields = new Dictionary<string, string?>(fieldCount);
            for (int k = 0; k < fieldCount; k++) { fields[arr[k]] = $"mod-{i}-{arr[k]}"; }
            _modifyCommands[i] = new UpsertRowCommand { CollectionId = CollectionId, Key = $"O{i + 1:D5}", Fields = fields };
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        var inserts = ViewEngineBenchmarks.BuildInsertCommands(N);

        var unfilteredView = new ViewDefinition { CollectionId = CollectionId };
        var sortedView = new ViewDefinition { CollectionId = CollectionId, SortColumn = "date", SortAscending = false };
        var filteredView = new ViewDefinition
        {
            CollectionId = CollectionId, SortColumn = "date", SortAscending = false,
            Filters = [new FilterSpec("category", FilterOperator.Eq, "category1")]
        };

        _engineNoSub        = Populate(ViewEngineBenchmarks.CreateEngine([]), inserts);
        _engineUnfiltered1  = Populate(ViewEngineBenchmarks.CreateEngine([unfilteredView]), inserts);
        _engineUnfiltered2  = Populate(ViewEngineBenchmarks.CreateEngine([unfilteredView, unfilteredView]), inserts);
        _engineUnfiltered10 = Populate(ViewEngineBenchmarks.CreateEngine(Enumerable.Repeat(unfilteredView, 10).ToArray()), inserts);
        _engineSorted1      = Populate(ViewEngineBenchmarks.CreateEngine([sortedView]), inserts);
        _engineSorted2      = Populate(ViewEngineBenchmarks.CreateEngine([sortedView, sortedView]), inserts);
        _engineSorted10     = Populate(ViewEngineBenchmarks.CreateEngine(Enumerable.Repeat(sortedView, 10).ToArray()), inserts);
        _engineFiltered1    = Populate(ViewEngineBenchmarks.CreateEngine([filteredView]), inserts);
        _engineFiltered2    = Populate(ViewEngineBenchmarks.CreateEngine([filteredView, filteredView]), inserts);
        _engineFiltered10   = Populate(ViewEngineBenchmarks.CreateEngine(Enumerable.Repeat(filteredView, 10).ToArray()), inserts);
        _engineSameSort4    = Populate(
            ViewEngineBenchmarks.CreateEngine(ViewEngineBenchmarks.BuildSameSortDifferentFilterViews(4)),
            inserts);
        _engineDiff2        = Populate(ViewEngineBenchmarks.CreateEngine(ViewEngineBenchmarks.BuildDistinctViews(2)), inserts);
        _engineDiff10       = Populate(ViewEngineBenchmarks.CreateEngine(ViewEngineBenchmarks.BuildDistinctViews(10)), inserts);
    }

    private static ViewEngine Populate(ViewEngine engine, UpsertRowCommand[] inserts)
    {
        foreach (var cmd in inserts) { engine.IngestAsync(cmd).GetAwaiter().GetResult(); }
        return engine;
    }

    [Benchmark] public async Task Modify10k_NoSubscribers()
    {
        foreach (var cmd in _modifyCommands) { await _engineNoSub.IngestAsync(cmd); }
    }

    [Benchmark] public async Task Modify10k_Unfiltered_1Subscriber()
    {
        foreach (var cmd in _modifyCommands) { await _engineUnfiltered1.IngestAsync(cmd); }
    }

    [Benchmark] public async Task Modify10k_Unfiltered_2Subscribers()
    {
        foreach (var cmd in _modifyCommands) { await _engineUnfiltered2.IngestAsync(cmd); }
    }

    [Benchmark] public async Task Modify10k_Unfiltered_10Subscribers()
    {
        foreach (var cmd in _modifyCommands) { await _engineUnfiltered10.IngestAsync(cmd); }
    }

    [Benchmark] public async Task Modify10k_SortedOnly_1Subscriber()
    {
        foreach (var cmd in _modifyCommands) { await _engineSorted1.IngestAsync(cmd); }
    }

    [Benchmark] public async Task Modify10k_SortedOnly_2Subscribers()
    {
        foreach (var cmd in _modifyCommands) { await _engineSorted2.IngestAsync(cmd); }
    }

    [Benchmark] public async Task Modify10k_SortedOnly_10Subscribers()
    {
        foreach (var cmd in _modifyCommands) { await _engineSorted10.IngestAsync(cmd); }
    }

    [Benchmark] public async Task Modify10k_SortedAndFiltered_1Subscriber()
    {
        foreach (var cmd in _modifyCommands) { await _engineFiltered1.IngestAsync(cmd); }
    }

    [Benchmark] public async Task Modify10k_SortedAndFiltered_2Subscribers()
    {
        foreach (var cmd in _modifyCommands) { await _engineFiltered2.IngestAsync(cmd); }
    }

    [Benchmark] public async Task Modify10k_SortedAndFiltered_10Subscribers()
    {
        foreach (var cmd in _modifyCommands) { await _engineFiltered10.IngestAsync(cmd); }
    }

    [Benchmark] public async Task Modify10k_SameSort_DifferentFilters_4Subscribers()
    {
        foreach (var cmd in _modifyCommands) { await _engineSameSort4.IngestAsync(cmd); }
    }

    [Benchmark] public async Task Modify10k_DifferentViews_2Subscribers()
    {
        foreach (var cmd in _modifyCommands) { await _engineDiff2.IngestAsync(cmd); }
    }

    [Benchmark] public async Task Modify10k_DifferentViews_10Subscribers()
    {
        foreach (var cmd in _modifyCommands) { await _engineDiff10.IngestAsync(cmd); }
    }
}

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "RatioSD")]
public class ViewEngineSnapshotBenchmarks
{
    private const int SnapshotRowCount = 30000;
    private ViewEngine _engine = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _engine = ViewEngineBenchmarks.CreateEngine([]);
        foreach (var command in ViewEngineBenchmarks.BuildInsertCommands(SnapshotRowCount))
        {
            _engine.IngestAsync(command).GetAwaiter().GetResult();
        }
    }

    [Benchmark]
    public Task StreamedSnapshot30k()
    {
        return _engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            View = new ViewDefinition { CollectionId = "objects" },
            StartIndex = 0,
            PageSize = SnapshotRowCount
        });
    }
}
