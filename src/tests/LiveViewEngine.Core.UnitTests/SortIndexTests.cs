using System.Buffers;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.UnitTests;

public class SortIndexTests
{
    private static (RowCollection collection, SortIndex index, int scoreFieldIndex, int activeFieldIndex)
        CreateSortedByScore(bool ascending = true)
    {
        var schema = new CollectionSchema("scores", ["score", "active"]);
        var col = new RowCollection(schema);
        var scoreFieldIndex = schema.GetFieldIndex("score");
        var activeFieldIndex = schema.GetFieldIndex("active");
        var index = new SortIndex(col, scoreFieldIndex, ascending);
        return (col, index, scoreFieldIndex, activeFieldIndex);
    }

    private static void Upsert(
        RowCollection col,
        SortIndex idx,
        string key,
        string score,
        string active = "true")
    {
        if (col.TryGetRowIndex(key, out int existingRowIndex))
        {
            idx.CaptureOldValue(existingRowIndex);
        }

        var mutation = col.AddOrUpdate(key, new Dictionary<string, string?> { ["score"] = score, ["active"] = active });
        idx.OnUpsert(mutation.RowIndex);
    }

    // Mirrors the paging logic that now lives in SharedView, for testing SortIndex behaviour.
    private static int[] GetPage(SortIndex idx, int startIndex, int? pageSize, FilterSet? filters = null)
    {
        if (startIndex < 0) { startIndex = 0; }
        if (pageSize is 0) { return []; }

        if (filters is { HasFilters: true })
        {
            int capacity = pageSize ?? idx.Count;
            var rented = ArrayPool<int>.Shared.Rent(capacity);
            try
            {
                int skipped = 0, count = 0;
                foreach (var rowIndex in idx.EnumerateFiltered(filters))
                {
                    if (skipped < startIndex) { skipped++; continue; }
                    rented[count++] = rowIndex;
                    if (pageSize.HasValue && count >= pageSize.Value) { break; }
                }
                var result = new int[count];
                rented.AsSpan(0, count).CopyTo(result);
                return result;
            }
            finally { ArrayPool<int>.Shared.Return(rented); }
        }

        int total = idx.Count;
        if (startIndex >= total) { return []; }
        int take = pageSize.HasValue ? Math.Min(pageSize.Value, total - startIndex) : total - startIndex;
        var page = new int[take];
        var cursor = idx.GetCursor(startIndex);
        for (int i = 0; i < take; i++) { cursor.MoveNext(); page[i] = cursor.Current; }
        return page;
    }

    [Fact]
    public void GetPageIndexes_AscendingOrder_ReturnsSortedValues()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, "a", "30");
        Upsert(col, idx, "b", "10");
        Upsert(col, idx, "c", "20");

        var indexes = GetPage(idx, 0, 10);
        var scores = indexes.Select(i => col.GetValue(i, scoreFieldIndex)).ToList();

        Assert.Equal(["10", "20", "30"], scores);
    }

    [Fact]
    public void GetPageIndexes_DescendingOrder_ReturnsSortedValues()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(false);
        Upsert(col, idx, "a", "30");
        Upsert(col, idx, "b", "10");
        Upsert(col, idx, "c", "20");

        var indexes = GetPage(idx, 0, 10);
        var scores = indexes.Select(i => col.GetValue(i, scoreFieldIndex)).ToList();

        Assert.Equal(["30", "20", "10"], scores);
    }

    [Fact]
    public void GetPageIndexes_UsesTypedScalarComparison_ForDeclaredIntField()
    {
        var schema = new CollectionSchema("scores", ["score"], [ScalarFieldType.Int32]);
        var collection = new RowCollection(schema);
        var index = new SortIndex(collection, schema.GetFieldIndex("score"), true);

        var upsertOne = collection.AddOrUpdate("a", new Dictionary<string, string?> { ["score"] = "12" });
        index.OnUpsert(upsertOne.RowIndex);
        var upsertTwo = collection.AddOrUpdate("b", new Dictionary<string, string?> { ["score"] = "2" });
        index.OnUpsert(upsertTwo.RowIndex);
        var upsertThree = collection.AddOrUpdate("c", new Dictionary<string, string?> { ["score"] = "8" });
        index.OnUpsert(upsertThree.RowIndex);

        var indexes = GetPage(index, 0, 10);
        var scores = indexes.Select(i => collection.GetValue(i, schema.GetFieldIndex("score"))).ToList();

        Assert.Equal(["2", "8", "12"], scores);
    }

    [Fact]
    public void GetPageIndexes_UsesTypedDateOnlyComparison_ForDeclaredDateOnlyField()
    {
        var schema = new CollectionSchema("events", ["day"], [ScalarFieldType.DateOnly]);
        var collection = new RowCollection(schema);
        var index = new SortIndex(collection, schema.GetFieldIndex("day"), true);

        var upsertOne = collection.AddOrUpdate("a", new Dictionary<string, string?> { ["day"] = "2025-01-15" });
        index.OnUpsert(upsertOne.RowIndex);
        var upsertTwo = collection.AddOrUpdate("b", new Dictionary<string, string?> { ["day"] = "2025-01-05" });
        index.OnUpsert(upsertTwo.RowIndex);
        var upsertThree = collection.AddOrUpdate("c", new Dictionary<string, string?> { ["day"] = "2025-01-10" });
        index.OnUpsert(upsertThree.RowIndex);

        var indexes = GetPage(index, 0, 10);
        var days = indexes.Select(i => collection.GetValue(i, schema.GetFieldIndex("day"))).ToList();

        Assert.Equal(["2025-01-05", "2025-01-10", "2025-01-15"], days);
    }

    [Fact]
    public void GetPageIndexes_OrdersCanonicalBooleanStrings_ForDeclaredBooleanField()
    {
        var schema = new CollectionSchema("flags", ["active"], [ScalarFieldType.Boolean]);
        var collection = new RowCollection(schema);
        var index = new SortIndex(collection, schema.GetFieldIndex("active"), true);

        var upsertOne = collection.AddOrUpdate("a", new Dictionary<string, string?> { ["active"] = "true" });
        index.OnUpsert(upsertOne.RowIndex);
        var upsertTwo = collection.AddOrUpdate("b", new Dictionary<string, string?> { ["active"] = "false" });
        index.OnUpsert(upsertTwo.RowIndex);
        var upsertThree = collection.AddOrUpdate("c", new Dictionary<string, string?> { ["active"] = "true" });
        index.OnUpsert(upsertThree.RowIndex);

        var indexes = GetPage(index, 0, 10);
        var values = indexes.Select(i => collection.GetValue(i, schema.GetFieldIndex("active"))).ToList();

        Assert.Equal(["false", "true", "true"], values);
    }

    [Fact]
    public void OnUpsert_UpdatedValue_ReordersIndex()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, "a", "2");
        Upsert(col, idx, "b", "4");

        Assert.True(col.TryGetRowIndex("b", out int existingRowIndex));
        idx.CaptureOldValue(existingRowIndex);
        var mutation = col.AddOrUpdate("b", new Dictionary<string, string?> { ["score"] = "1" });
        idx.OnUpsert(mutation.RowIndex);

        var indexes = GetPage(idx, 0, 10);
        Assert.Equal("1", col.GetValue(indexes[0], scoreFieldIndex));
        Assert.Equal("2", col.GetValue(indexes[1], scoreFieldIndex));
    }

    [Fact]
    public void OnDelete_RemovesRowFromIndex()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, "a", "1");
        Upsert(col, idx, "b", "2");

        Assert.True(col.TryGetRowIndex("a", out int existingRowIndex));
        idx.CaptureOldValue(existingRowIndex);
        var deleted = col.Delete("a");
        idx.OnDelete(deleted!.RowIndex);

        var indexes = GetPage(idx, 0, 10);
        Assert.Single(indexes);
        Assert.Equal("b", col.GetRowId(indexes[0]));
    }

    [Fact]
    public void GetPageIndexes_WithFilter_ReturnsOnlyMatchingRows()
    {
        var (col, idx, scoreFieldIndex, activeFieldIndex) = CreateSortedByScore(true);
        Upsert(col, idx, "a", "10", "true");
        Upsert(col, idx, "b", "20", "false");
        Upsert(col, idx, "c", "30", "true");

        var filter = new FilterSpec("active", FilterOperator.Eq, "true");
        var indexes = GetPage(idx, 0, 10, FilterSet.Create([filter], col.Schema));

        Assert.Equal(2, indexes.Length);
    }

    [Fact]
    public void GetPageIndexes_EqualValues_AreOrderedByIndex()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, "a", "10");
        Upsert(col, idx, "b", "10");
        Upsert(col, idx, "c", "10");

        var indexes = GetPage(idx, 0, 10);

        Assert.Equal(3, indexes.Length);
        Assert.True(indexes[0] < indexes[1]);
        Assert.True(indexes[1] < indexes[2]);
    }

    [Fact]
    public void GetPageIndexes_WithStartIndex_SkipsCorrectRows()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, "a", "10");
        Upsert(col, idx, "b", "20");
        Upsert(col, idx, "c", "30");

        var indexes = GetPage(idx, 1, 10);
        var scores = indexes.Select(i => col.GetValue(i, scoreFieldIndex)).ToList();

        Assert.Equal(["20", "30"], scores);
    }

    [Fact]
    public void GetPageIndexes_PageSmallerThanTotal_ReturnsExactCount()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, "a", "10");
        Upsert(col, idx, "b", "20");
        Upsert(col, idx, "c", "30");

        var indexes = GetPage(idx, 0, 2);
        Assert.Equal(2, indexes.Length);
    }

    [Fact]
    public void GetPageIndexes_NegativeStartIndex_TreatedAsZero()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, "a", "10");
        Upsert(col, idx, "b", "20");

        var indexes = GetPage(idx, -5, 10);
        Assert.Equal(2, indexes.Length);
        Assert.Equal("10", col.GetValue(indexes[0], scoreFieldIndex));
    }

    [Fact]
    public void GetPageIndexes_ZeroPageSize_ReturnsEmpty()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, "a", "10");

        var indexes = GetPage(idx, 0, 0);
        Assert.Empty(indexes);
    }

    [Fact]
    public void GetPageIndexes_StartIndexBeyondEnd_ReturnsEmpty()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, "a", "10");

        var indexes = GetPage(idx, 5, 10);
        Assert.Empty(indexes);
    }

    [Fact]
    public void GetPageIndexes_FilteredWithStartIndex_SkipsFilteredRows()
    {
        var (col, idx, scoreFieldIndex, activeFieldIndex) = CreateSortedByScore(true);
        Upsert(col, idx, "a", "10", "true");
        Upsert(col, idx, "b", "20", "true");
        Upsert(col, idx, "c", "30", "true");
        Upsert(col, idx, "d", "40", "false");

        var filter = new FilterSpec("active", FilterOperator.Eq, "true");
        var indexes = GetPage(idx, 1, 10, FilterSet.Create([filter], col.Schema));
        var scores = indexes.Select(i => col.GetValue(i, scoreFieldIndex)).ToList();

        Assert.Equal(["20", "30"], scores);
    }

    [Fact]
    public void OnUpsert_UpdatedTypedValue_ReordersIndex()
    {
        var schema = new CollectionSchema("scores", ["score"], [ScalarFieldType.Int32]);
        var col = new RowCollection(schema);
        var scoreFieldIndex = schema.GetFieldIndex("score");
        var idx = new SortIndex(col, scoreFieldIndex, true);

        var m1 = col.AddOrUpdate("a", new Dictionary<string, string?> { ["score"] = "10" });
        idx.OnUpsert(m1.RowIndex);
        var m2 = col.AddOrUpdate("b", new Dictionary<string, string?> { ["score"] = "20" });
        idx.OnUpsert(m2.RowIndex);
        var m3 = col.AddOrUpdate("c", new Dictionary<string, string?> { ["score"] = "30" });
        idx.OnUpsert(m3.RowIndex);

        // Update "c" from 30 to 5 — must reposition from last to first
        col.TryGetRowIndex("c", out var cIndex);
        idx.CaptureOldValue(cIndex);
        var updated = col.AddOrUpdate("c", new Dictionary<string, string?> { ["score"] = "5" });
        idx.OnUpsert(updated.RowIndex);

        var indexes = GetPage(idx, 0, 10);
        var scores = indexes.Select(i => col.GetValue(i, scoreFieldIndex)).ToList();

        Assert.Equal(["5", "10", "20"], scores);
    }

    [Fact]
    public void OnUpsert_ReusedSlot_TypedColumnClearedBeforeNewValues()
    {
        var schema = new CollectionSchema("scores", ["score"], [ScalarFieldType.Int32]);
        var col = new RowCollection(schema);
        var scoreFieldIndex = schema.GetFieldIndex("score");
        col.ActivateTypedField(scoreFieldIndex);

        // Insert and delete "a" (score=99) to free its slot
        var m1 = col.AddOrUpdate("a", new Dictionary<string, string?> { ["score"] = "99" });
        var deleted = col.Delete("a");

        // Insert "b" without a score into the reused slot — typed value must be null, not stale 99
        col.AddOrUpdate("b", new Dictionary<string, string?> { });
        col.TryGetRowIndex("b", out var bIndex);

        Assert.Equal(m1.RowIndex, bIndex); // confirm slot was reused
        Assert.Null(col.GetInt32(bIndex, scoreFieldIndex));
    }
}
