using ViewEngineServer.Core;

namespace ViewEngineServer.UnitTests.Indexing;

public class SortIndexTests
{
    private static (ColumnarCollection collection, SortIndex index) CreateSortedByScore(bool ascending = true)
    {
        var schema = new CollectionSchema
        {
            CollectionId = "scores",
            Fields =
            [
                new FieldDefinition("id", FieldType.String, IsPrimaryKey: true),
                new FieldDefinition("score", FieldType.Int32, IsSortable: true)
            ]
        };
        var col = new ColumnarCollection(schema);
        var index = new SortIndex(col, fieldIndex: 1, ascending: ascending);
        return (col, index);
    }

    private static void Upsert(ColumnarCollection col, SortIndex idx, string id, int score)
    {
        var mut = col.Upsert(new Dictionary<string, object?> { ["id"] = id, ["score"] = score });
        idx.OnUpsert(mut.Handle, mut.NewValues?[1]);
    }


    [Fact]
    public void GetPageHandles_AscendingOrder_ReturnsSortedHandles()
    {
        var (col, idx) = CreateSortedByScore(ascending: true);
        Upsert(col, idx, "a", 30);
        Upsert(col, idx, "b", 10);
        Upsert(col, idx, "c", 20);

        var handles = idx.GetPageHandles(0, 10);
        var scores = handles.Select(h => col.GetValue(h, 1)).ToList();

        Assert.Equal(["10", "20", "30"], scores);
    }

    [Fact]
    public void GetPageHandles_DescendingOrder_ReturnsSortedHandles()
    {
        var (col, idx) = CreateSortedByScore(ascending: false);
        Upsert(col, idx, "a", 30);
        Upsert(col, idx, "b", 10);
        Upsert(col, idx, "c", 20);

        var handles = idx.GetPageHandles(0, 10);
        var scores = handles.Select(h => col.GetValue(h, 1)).ToList();

        Assert.Equal(["30", "20", "10"], scores);
    }


    [Fact]
    public void GetPageHandles_SecondPage_ReturnsCorrectSubset()
    {
        var (col, idx) = CreateSortedByScore();
        for (int i = 1; i <= 5; i++)
        {
            Upsert(col, idx, $"r{i}", i * 10);
        }

        var page2 = idx.GetPageHandles(2, 2);
        var scores = page2.Select(h => col.GetValue(h, 1)).ToList();
        Assert.Equal(["30", "40"], scores);
    }

    [Fact]
    public void GetPageHandles_BeyondEnd_ReturnsPartialPage()
    {
        var (col, idx) = CreateSortedByScore();
        Upsert(col, idx, "a", 1);
        Upsert(col, idx, "b", 2);

        var page = idx.GetPageHandles(1, 10);
        Assert.Single(page);
    }


    [Fact]
    public void OnUpsert_UpdatedScore_ReordersIndex()
    {
        var (col, idx) = CreateSortedByScore();
        Upsert(col, idx, "a", 10);
        Upsert(col, idx, "b", 30);

        var mut = col.Upsert(new Dictionary<string, object?> { ["id"] = "b", ["score"] = 5 });
        idx.OnUpsert(mut.Handle, mut.NewValues?[1]);

        var handles = idx.GetPageHandles(0, 10);
        Assert.Equal(5, (int)col.GetValue(handles[0], 1)!);
        Assert.Equal(10, (int)col.GetValue(handles[1], 1)!);
    }

    [Fact]
    public void OnDelete_RemovedRow_NotReturnedInPage()
    {
        var (col, idx) = CreateSortedByScore();
        var r1 = col.Upsert(new Dictionary<string, object?> { ["id"] = "a", ["score"] = 1 });
        idx.OnUpsert(r1.Handle, r1.NewValues?[1]);
        var r2 = col.Upsert(new Dictionary<string, object?> { ["id"] = "b", ["score"] = 2 });
        idx.OnUpsert(r2.Handle, r2.NewValues?[1]);

        var del = col.Delete("a");
        idx.OnDelete(del!.Handle);

        var handles = idx.GetPageHandles(0, 10);
        Assert.Single(handles);
        Assert.Equal("b", col.GetRowId(handles[0]));
    }


    [Fact]
    public void GetPageHandles_WithFilter_ExcludesNonMatchingRows()
    {
        var schema = new CollectionSchema
        {
            CollectionId = "c",
            Fields =
            [
                new FieldDefinition("id", FieldType.String, IsPrimaryKey: true),
                new FieldDefinition("score", FieldType.Int32, IsSortable: true),
                new FieldDefinition("active", FieldType.Boolean, IsFilterable: true)
            ]
        };
        var col = new ColumnarCollection(schema);
        var idx = new SortIndex(col, 1, ascending: true);

        var insert = (string id, int score, bool active) =>
        {
            var m = col.Upsert(new Dictionary<string, object?> { ["id"] = id, ["score"] = score, ["active"] = active });
            idx.OnUpsert(m.Handle, m.NewValues?[1]);
        };

        insert("a", 10, true);
        insert("b", 20, false);
        insert("c", 30, true);

        var filter = new FilterSpec("active", FilterOperator.Eq, true);
        var handles = idx.GetPageHandles(0, 10, [filter], [2]);
        Assert.Equal(2, handles.Length);
    }


    [Fact]
    public void GetCount_NoFilter_ReturnsAllRows()
    {
        var (col, idx) = CreateSortedByScore();
        Upsert(col, idx, "a", 1);
        Upsert(col, idx, "b", 2);
        Assert.Equal(2, idx.GetCount());
    }

    [Fact]
    public void GetCount_AfterDelete_Decrements()
    {
        var (col, idx) = CreateSortedByScore();
        var r = col.Upsert(new Dictionary<string, object?> { ["id"] = "a", ["score"] = 1 });
        idx.OnUpsert(r.Handle, r.NewValues?[1]);
        var del = col.Delete("a");
        idx.OnDelete(del!.Handle);
        Assert.Equal(0, idx.GetCount());
    }
}
