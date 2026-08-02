namespace LiveViewEngine.Core.UnitTests;

public class SortIndexTests
{
    private static (RowCollection collection, SortIndex index) CreateSortedByScore(bool ascending = true)
    {
        var schema = new CollectionSchema
        {
            CollectionId = "scores",
            Fields =
            [
                new FieldDefinition("id", FieldType.String, IsPrimaryKey: true),
                new FieldDefinition("score", FieldType.String, IsSortable: true)
            ]
        };
        var col = new RowCollection(schema);
        var index = new SortIndex(col, fieldIndex: 1, ascending: ascending);
        return (col, index);
    }

    private static void Upsert(RowCollection col, SortIndex idx, string id, string score)
    {
        var mut = col.Upsert(new Dictionary<string, string?> { ["id"] = id, ["score"] = score });
        idx.OnUpsert(mut.Index, score);
    }


    [Fact]
    public void GetPageIndexes_AscendingOrder_ReturnsSortedIndexes()
    {
        var (col, idx) = CreateSortedByScore(ascending: true);
        Upsert(col, idx, "a", "30");
        Upsert(col, idx, "b", "10");
        Upsert(col, idx, "c", "20");

        var indexes = idx.GetPageIndexes(0, 10);
        var scores = indexes.Select(i => col.GetValue(i, 1)).ToList();

        Assert.Equal(["10", "20", "30"], scores);
    }

    [Fact]
    public void GetPageIndexes_DescendingOrder_ReturnsSortedIndexes()
    {
        var (col, idx) = CreateSortedByScore(ascending: false);
        Upsert(col, idx, "a", "30");
        Upsert(col, idx, "b", "10");
        Upsert(col, idx, "c", "20");

        var indexes = idx.GetPageIndexes(0, 10);
        var scores = indexes.Select(i => col.GetValue(i, 1)).ToList();

        Assert.Equal(["30", "20", "10"], scores);
    }


    [Fact]
    public void GetPageIndexes_SecondPage_ReturnsCorrectSubset()
    {
        var (col, idx) = CreateSortedByScore();
        for (int i = 1; i <= 5; i++)
        {
            Upsert(col, idx, $"r{i}", $"{i * 10}");
        }

        var page2 = idx.GetPageIndexes(2, 2);
        var scores = page2.Select(i => col.GetValue(i, 1)).ToList();
        Assert.Equal(["30", "40"], scores);
    }

    [Fact]
    public void GetPageIndexes_BeyondEnd_ReturnsPartialPage()
    {
        var (col, idx) = CreateSortedByScore();
        Upsert(col, idx, "a", "1");
        Upsert(col, idx, "b", "2");

        var page = idx.GetPageIndexes(1, 10);
        Assert.Single(page);
    }


    [Fact]
    public void OnUpsert_UpdatedScore_ReordersIndex()
    {
        var (col, idx) = CreateSortedByScore();
        Upsert(col, idx, "a", "2");
        Upsert(col, idx, "b", "4");

        var mut = col.Upsert(new Dictionary<string, string?> { ["id"] = "b", ["score"] = "1" });
        idx.OnUpsert(mut.Index, "1");

        var indexes = idx.GetPageIndexes(0, 10);
        Assert.Equal("1", col.GetValue(indexes[0], 1));
        Assert.Equal("2", col.GetValue(indexes[1], 1));
    }

    [Fact]
    public void OnDelete_RemovedRow_NotReturnedInPage()
    {
        var (col, idx) = CreateSortedByScore();
        var r1 = col.Upsert(new Dictionary<string, string?> { ["id"] = "a", ["score"] = "1" });
        idx.OnUpsert(r1.Index, "1");
        var r2 = col.Upsert(new Dictionary<string, string?> { ["id"] = "b", ["score"] = "2" });
        idx.OnUpsert(r2.Index, "2");

        var del = col.Delete("a");
        idx.OnDelete(del!.Index);

        var indexes = idx.GetPageIndexes(0, 10);
        Assert.Single(indexes);
        Assert.Equal("b", col.GetRowId(indexes[0]));
    }


    [Fact]
    public void GetPageIndexes_WithFilter_ExcludesNonMatchingRows()
    {
        var schema = new CollectionSchema
        {
            CollectionId = "c",
            Fields =
            [
                new FieldDefinition("id", FieldType.String, IsPrimaryKey: true),
                new FieldDefinition("score", FieldType.String, IsSortable: true),
                new FieldDefinition("active", FieldType.String, IsFilterable: true)
            ]
        };
        var col = new RowCollection(schema);
        var idx = new SortIndex(col, 1, ascending: true);

        var insert = (string id, string score, string active) =>
        {
            var m = col.Upsert(new Dictionary<string, string?> { ["id"] = id, ["score"] = score, ["active"] = active });
            idx.OnUpsert(m.Index, score);
        };

        insert("a", "10", "true");
        insert("b", "20", "false");
        insert("c", "30", "true");

        var filter = new FilterSpec("active", FilterOperator.Eq, "true");
        var indexes = idx.GetPageIndexes(0, 10, [filter], [2]);
        Assert.Equal(2, indexes.Length);
    }


    [Fact]
    public void GetCount_NoFilter_ReturnsAllRows()
    {
        var (col, idx) = CreateSortedByScore();
        Upsert(col, idx, "a", "1");
        Upsert(col, idx, "b", "2");
        Assert.Equal(2, idx.GetCount());
    }

    [Fact]
    public void GetCount_AfterDelete_Decrements()
    {
        var (col, idx) = CreateSortedByScore();
        var r = col.Upsert(new Dictionary<string, string?> { ["id"] = "a", ["score"] = "1" });
        idx.OnUpsert(r.Index, "1");
        var del = col.Delete("a");
        idx.OnDelete(del!.Index);
        Assert.Equal(0, idx.GetCount());
    }
}
