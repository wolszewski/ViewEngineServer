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
        int scoreFieldIndex,
        string key,
        string score,
        string active = "true")
    {
        var mutation = col.AddOrUpdate(key, new Dictionary<string, string?> { ["score"] = score, ["active"] = active });
        idx.OnUpsert(mutation.Index, col.GetValue(mutation.Index, scoreFieldIndex));
    }

    [Fact]
    public void GetPageIndexes_AscendingOrder_ReturnsSortedValues()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, scoreFieldIndex, "a", "30");
        Upsert(col, idx, scoreFieldIndex, "b", "10");
        Upsert(col, idx, scoreFieldIndex, "c", "20");

        var indexes = idx.GetPageIndexes(0, 10);
        var scores = indexes.Select(i => col.GetValue(i, scoreFieldIndex)).ToList();

        Assert.Equal(["10", "20", "30"], scores);
    }

    [Fact]
    public void GetPageIndexes_DescendingOrder_ReturnsSortedValues()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(false);
        Upsert(col, idx, scoreFieldIndex, "a", "30");
        Upsert(col, idx, scoreFieldIndex, "b", "10");
        Upsert(col, idx, scoreFieldIndex, "c", "20");

        var indexes = idx.GetPageIndexes(0, 10);
        var scores = indexes.Select(i => col.GetValue(i, scoreFieldIndex)).ToList();

        Assert.Equal(["30", "20", "10"], scores);
    }

    [Fact]
    public void OnUpsert_UpdatedValue_ReordersIndex()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, scoreFieldIndex, "a", "2");
        Upsert(col, idx, scoreFieldIndex, "b", "4");

        var mutation = col.AddOrUpdate("b", new Dictionary<string, string?> { ["score"] = "1" });
        idx.OnUpsert(mutation.Index, col.GetValue(mutation.Index, scoreFieldIndex));

        var indexes = idx.GetPageIndexes(0, 10);
        Assert.Equal("1", col.GetValue(indexes[0], scoreFieldIndex));
        Assert.Equal("2", col.GetValue(indexes[1], scoreFieldIndex));
    }

    [Fact]
    public void OnDelete_RemovesRowFromIndex()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, scoreFieldIndex, "a", "1");
        Upsert(col, idx, scoreFieldIndex, "b", "2");

        var deleted = col.Delete("a");
        idx.OnDelete(deleted!.Index);

        var indexes = idx.GetPageIndexes(0, 10);
        Assert.Single(indexes);
        Assert.Equal("b", col.GetRowId(indexes[0]));
    }

    [Fact]
    public void GetPageIndexes_WithFilter_ReturnsOnlyMatchingRows()
    {
        var (col, idx, scoreFieldIndex, activeFieldIndex) = CreateSortedByScore(true);
        Upsert(col, idx, scoreFieldIndex, "a", "10", "true");
        Upsert(col, idx, scoreFieldIndex, "b", "20", "false");
        Upsert(col, idx, scoreFieldIndex, "c", "30", "true");

        var filter = new FilterSpec("active", FilterOperator.Eq, "true");
        var indexes = idx.GetPageIndexes(0, 10, [filter], [activeFieldIndex]);

        Assert.Equal(2, indexes.Length);
    }

    [Fact]
    public void GetPageIndexes_EqualValues_AreOrderedByIndex()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, scoreFieldIndex, "a", "10");
        Upsert(col, idx, scoreFieldIndex, "b", "10");
        Upsert(col, idx, scoreFieldIndex, "c", "10");

        var indexes = idx.GetPageIndexes(0, 10);

        Assert.Equal(3, indexes.Length);
        Assert.True(indexes[0] < indexes[1]);
        Assert.True(indexes[1] < indexes[2]);
    }

    [Fact]
    public void GetPageIndexes_WithStartIndex_SkipsCorrectRows()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, scoreFieldIndex, "a", "10");
        Upsert(col, idx, scoreFieldIndex, "b", "20");
        Upsert(col, idx, scoreFieldIndex, "c", "30");

        var indexes = idx.GetPageIndexes(1, 10);
        var scores = indexes.Select(i => col.GetValue(i, scoreFieldIndex)).ToList();

        Assert.Equal(["20", "30"], scores);
    }

    [Fact]
    public void GetPageIndexes_PageSmallerThanTotal_ReturnsExactCount()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, scoreFieldIndex, "a", "10");
        Upsert(col, idx, scoreFieldIndex, "b", "20");
        Upsert(col, idx, scoreFieldIndex, "c", "30");

        var indexes = idx.GetPageIndexes(0, 2);
        Assert.Equal(2, indexes.Length);
    }

    [Fact]
    public void GetPageIndexes_NegativeStartIndex_TreatedAsZero()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, scoreFieldIndex, "a", "10");
        Upsert(col, idx, scoreFieldIndex, "b", "20");

        var indexes = idx.GetPageIndexes(-5, 10);
        Assert.Equal(2, indexes.Length);
        Assert.Equal("10", col.GetValue(indexes[0], scoreFieldIndex));
    }

    [Fact]
    public void GetPageIndexes_ZeroPageSize_ReturnsEmpty()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, scoreFieldIndex, "a", "10");

        var indexes = idx.GetPageIndexes(0, 0);
        Assert.Empty(indexes);
    }

    [Fact]
    public void GetPageIndexes_StartIndexBeyondEnd_ReturnsEmpty()
    {
        var (col, idx, scoreFieldIndex, _) = CreateSortedByScore(true);
        Upsert(col, idx, scoreFieldIndex, "a", "10");

        var indexes = idx.GetPageIndexes(5, 10);
        Assert.Empty(indexes);
    }

    [Fact]
    public void GetPageIndexes_FilteredWithStartIndex_SkipsFilteredRows()
    {
        var (col, idx, scoreFieldIndex, activeFieldIndex) = CreateSortedByScore(true);
        Upsert(col, idx, scoreFieldIndex, "a", "10", "true");
        Upsert(col, idx, scoreFieldIndex, "b", "20", "true");
        Upsert(col, idx, scoreFieldIndex, "c", "30", "true");
        Upsert(col, idx, scoreFieldIndex, "d", "40", "false");

        var filter = new FilterSpec("active", FilterOperator.Eq, "true");
        var indexes = idx.GetPageIndexes(1, 10, [filter], [activeFieldIndex]);
        var scores = indexes.Select(i => col.GetValue(i, scoreFieldIndex)).ToList();

        Assert.Equal(["20", "30"], scores);
    }
}
