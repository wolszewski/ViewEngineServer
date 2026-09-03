using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.UnitTests;

public class NaturalOrderIndexTests
{
    private static (RowCollection collection, NaturalOrderIndex index) CreateEmpty()
    {
        var schema = new CollectionSchema("rows", ["label"]);
        var col = new RowCollection(schema);
        return (col, new NaturalOrderIndex(col));
    }

    private static int Upsert(RowCollection col, NaturalOrderIndex idx, string key, string label)
    {
        var mutation = col.AddOrUpdate(key, new Dictionary<string, string?> { ["label"] = label });
        idx.OnUpsert(mutation.RowIndex);
        return mutation.RowIndex;
    }

    [Fact]
    public void FieldIndex_IsNegativeOne()
    {
        var (_, idx) = CreateEmpty();
        Assert.Equal(-1, idx.FieldIndex);
    }

    [Fact]
    public void AffectsOrder_IsAlwaysFalse()
    {
        var (_, idx) = CreateEmpty();
        var mask = FieldMask.From([0, 1, 2]);
        Assert.False(idx.AffectsOrder(mask));
    }

    [Fact]
    public void OnUpsert_AssignsPositionsInArrivalOrder()
    {
        var (col, idx) = CreateEmpty();
        Upsert(col, idx, "c", "C");
        Upsert(col, idx, "a", "A");
        Upsert(col, idx, "b", "B");

        var destination = new int[3];
        idx.Take(0, destination);
        var keys = destination.Select(rowIndex => col.GetValue(rowIndex, 0)).ToList();

        Assert.Equal(["c", "a", "b"], keys);
    }

    [Fact]
    public void OnUpsert_ExistingRow_DoesNotChangePosition()
    {
        var (col, idx) = CreateEmpty();
        var cRow = Upsert(col, idx, "c", "C");
        Upsert(col, idx, "a", "A");

        Assert.Equal(0, idx.IndexOf(cRow));

        col.AddOrUpdate("c", new Dictionary<string, string?> { ["label"] = "C-updated" });
        idx.OnUpsert(cRow); // existing row: must be a no-op for position.

        Assert.Equal(0, idx.IndexOf(cRow));
        Assert.Equal(2, idx.Count);
    }

    [Fact]
    public void OnDelete_RemovesRowAndCompactsPositions()
    {
        var (col, idx) = CreateEmpty();
        Upsert(col, idx, "c", "C");
        var aRow = Upsert(col, idx, "a", "A");
        var bRow = Upsert(col, idx, "b", "B");

        var cRow = col.TryGetRowIndex("c", out var existing) ? existing : -1;
        idx.OnDelete(cRow);

        Assert.Equal(2, idx.Count);
        Assert.Equal(0, idx.IndexOf(aRow));
        Assert.Equal(1, idx.IndexOf(bRow));
    }

    [Fact]
    public void OnDelete_ThenReinsert_GetsFreshPositionAtEnd()
    {
        var (col, idx) = CreateEmpty();
        var aRow = Upsert(col, idx, "a", "A");
        Upsert(col, idx, "b", "B");

        col.Delete("a");
        idx.OnDelete(aRow);

        var newARow = Upsert(col, idx, "a", "A2");

        Assert.Equal(2, idx.Count);
        Assert.Equal(1, idx.IndexOf(newARow));
    }

    [Fact]
    public void LazyConstruction_AfterDeleteAndReinsertChurn_ReflectsTrueArrivalOrder()
    {
        // Simulates a NaturalOrderIndex built lazily (first no-sortColumn subscribe) after the
        // collection has already seen delete+reinsert churn — no index observed the mutations as
        // they happened, so the constructor must not rely on RowCollection's live-index (dictionary)
        // enumeration order, which is perturbed by slot reuse: deleting "b" frees its row slot, and
        // inserting "d" afterward can reuse that same slot, which would otherwise place "d" before
        // "c" (an older, still-live row) instead of after it.
        var schema = new CollectionSchema("rows", ["label"]);
        var col = new RowCollection(schema);

        col.AddOrUpdate("a", new Dictionary<string, string?> { ["label"] = "A" });
        col.AddOrUpdate("b", new Dictionary<string, string?> { ["label"] = "B" });
        col.AddOrUpdate("c", new Dictionary<string, string?> { ["label"] = "C" });
        col.Delete("b");
        col.AddOrUpdate("d", new Dictionary<string, string?> { ["label"] = "D" });

        var idx = new NaturalOrderIndex(col);

        var destination = new int[idx.Count];
        idx.Take(0, destination);
        var keys = destination.Select(rowIndex => col.GetValue(rowIndex, 0)).ToList();

        Assert.Equal(["a", "c", "d"], keys);
    }
}
