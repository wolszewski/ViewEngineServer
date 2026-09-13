using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using LiveViewEngine.Core.DataIngest;
using LiveViewEngine.Core.Views;

namespace LiveViewEngine.Core.Benchmarks;

// The Insert* benchmarks all take the IsNew short-circuit in MutationPropagator, so they never
// reach the changed-columns predicates (AffectsOrder / ContainsAny). These cover updates to
// existing rows, which is the path those predicates actually run on.
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "RatioSD")]
public class ViewEngineUpdateBenchmarks
{
    private const string CollectionId = "objects";
    private const int RowCount = 10_000;
    private const int UpdateCount = 10_000;

    // Confined to rows inside every subscriber's page, so updates produce real deltas rather than
    // being dropped by the viewport check.
    private const int HotRows = 50;

    private static UpsertRowCommand[] BuildUpdates(string field) =>
        [.. Enumerable.Range(0, UpdateCount).Select(i => new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = $"O{(i % HotRows) + 1:D5}",
            Fields = new Dictionary<string, string?> { [field] = $"u{i % 97}" }
        })];

    private UpsertRowCommand[] _visibleFieldUpdates = [];
    private UpsertRowCommand[] _sortFieldUpdates = [];
    private ViewEngine _unfiltered1 = null!;
    private ViewEngine _sorted10 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _visibleFieldUpdates = BuildUpdates("f15");
        _sortFieldUpdates = BuildUpdates("date");

        var unfiltered = new ViewDefinition { CollectionId = CollectionId };
        var sorted = new ViewDefinition { CollectionId = CollectionId, SortColumn = "date", SortAscending = false };

        _unfiltered1 = Seed(ViewEngineBenchmarks.CreateEngine([unfiltered]));
        _sorted10 = Seed(ViewEngineBenchmarks.CreateEngine([.. Enumerable.Repeat(sorted, 10)]));
    }

    private static ViewEngine Seed(ViewEngine engine)
    {
        foreach (var command in ViewEngineBenchmarks.BuildInsertCommands(RowCount))
        {
            engine.IngestAsync(command).GetAwaiter().GetResult();
        }

        return engine;
    }

    // Fast path: the changed field is neither sorted nor filtered, so the mutation is checked
    // against each subscriber's visible columns and emitted as a RowUpdateDelta.
    [Benchmark]
    public async Task Update10k_NonSortField_Unfiltered_1Subscriber()
    {
        foreach (var command in _visibleFieldUpdates) { await _unfiltered1.IngestAsync(command); }
    }

    [Benchmark]
    public async Task Update10k_NonSortField_Sorted_10Subscribers()
    {
        foreach (var command in _visibleFieldUpdates) { await _sorted10.IngestAsync(command); }
    }

    // Slow path: the changed field drives the sort, so every update reorders and recomputes.
    [Benchmark]
    public async Task Update10k_SortField_Sorted_10Subscribers()
    {
        foreach (var command in _sortFieldUpdates) { await _sorted10.IngestAsync(command); }
    }
}
