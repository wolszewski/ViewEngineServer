using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.DataIngest;
using LiveViewEngine.Core.Output;
using LiveViewEngine.Core.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveViewEngine.Core.IntegrationTests;

// Regression coverage for a PR review finding: custom IRowProjector-based redaction was applied to
// snapshots/inserts/replacements but bypassed for RowUpdateDelta, since the fast-path and
// position-recompute update paths only filtered ChangedColumns by column visibility, never running
// them through the configured IRowProjector.
public class MutationPropagatorProjectionTests
{
    private static (ViewEngine engine, CapturingPublisher publisher) CreateEngine()
    {
        var metrics = new ViewEngineMetrics();
        var options = new LiveViewEngineOptions
        {
            EagerIndexing = false,
            RowProjector = new RedactingRowProjector(WidgetsSchema().GetFieldIndex("secret"))
        };
        var store = new CollectionStore(metrics, options);
        var publisher = new CapturingPublisher();
        var engine = new ViewEngine(store, publisher, NullLogger<ViewEngine>.Instance, metrics);
        return (engine, publisher);
    }

    private static CollectionSchema WidgetsSchema() => new("widgets", ["id", "sortKey", "secret"]);

    private static Task<IngestResult> CreateWidgets(ViewEngine engine) =>
        engine.IngestAsync(new CreateCollectionCommand { CollectionId = "widgets", Schema = WidgetsSchema() });

    private static Task<IngestResult> UpsertWidget(ViewEngine engine, string key, string sortKey, string secret) =>
        engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "widgets",
            Key = key,
            Fields = new Dictionary<string, string?> { ["sortKey"] = sortKey, ["secret"] = secret }
        });

    [Fact]
    public async Task CustomProjector_MasksRedactedField_OnFastPathUpdate()
    {
        var (engine, publisher) = CreateEngine();
        await CreateWidgets(engine);
        await UpsertWidget(engine, "w1", "A", "raw-initial");

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition { CollectionId = "widgets" },
            StartIndex = 0,
            PageSize = 10
        });

        // Only "secret" changes; sortKey is untouched and there is no sort/filter configured, so this
        // stays on the fast path (CollectFastPathGroups).
        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "widgets",
            Key = "w1",
            Fields = new Dictionary<string, string?> { ["secret"] = "raw-updated" }
        });

        var update = Assert.Single(publisher.EventsFor(1).OfType<RowUpdateEvent>());
        Assert.Equal("***", update.ChangedFields["secret"]);
    }

    [Fact]
    public async Task CustomProjector_MasksRedactedField_OnPositionRecomputeUpdate()
    {
        var (engine, publisher) = CreateEngine();
        await CreateWidgets(engine);
        await UpsertWidget(engine, "w1", "A", "raw-initial");

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition { CollectionId = "widgets", SortColumn = "sortKey" },
            StartIndex = 0,
            PageSize = 10
        });

        // Changing the sort field forces a full recompute (CollectPositionGroups); with only one row
        // in the collection the filtered position stays 0, so this exercises the stable-position
        // BuildUpdateDeltaIfVisible branch rather than an insert/replace/remove.
        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "widgets",
            Key = "w1",
            Fields = new Dictionary<string, string?> { ["sortKey"] = "B", ["secret"] = "raw-updated" }
        });

        var update = Assert.Single(publisher.EventsFor(1).OfType<RowUpdateEvent>());
        Assert.Equal("***", update.ChangedFields["secret"]);
        Assert.Equal("B", update.ChangedFields["sortKey"]);
    }

    private sealed class RedactingRowProjector(int redactedFieldIndex) : IRowProjector
    {
        public string?[] Project(string?[] source, int[] selectedFieldIndexes)
        {
            var copy = new string?[selectedFieldIndexes.Length];
            for (int i = 0; i < selectedFieldIndexes.Length; i++)
            {
                var fieldIndex = selectedFieldIndexes[i];
                copy[i] = fieldIndex == redactedFieldIndex ? "***" : source[fieldIndex];
            }

            return copy;
        }
    }
}
