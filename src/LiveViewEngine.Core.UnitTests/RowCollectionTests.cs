using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.UnitTests;

public class RowCollectionTests
{
    private static RowCollection CreateCollection() =>
        new(new CollectionSchema("test", ["name", "score"]));

    [Fact]
    public void AddOrUpdate_NewRow_ReturnsIsNewTrue()
    {
        var col = CreateCollection();
        var result = col.AddOrUpdate("r1", new Dictionary<string, string?> { ["name"] = "Alice", ["score"] = "10" });

        Assert.True(result.IsNew);
        Assert.Equal("r1", result.RowId);
        Assert.NotEmpty(result.ChangedColumns!);
    }

    [Fact]
    public void AddOrUpdate_EmptyKey_Throws()
    {
        var col = CreateCollection();
        Assert.Throws<ArgumentException>(() => col.AddOrUpdate("", new Dictionary<string, string?>()));
    }

    [Fact]
    public void AddOrUpdate_ExistingRow_ReusesIndex()
    {
        var col = CreateCollection();
        var first = col.AddOrUpdate("r1", new Dictionary<string, string?> { ["name"] = "Alice" });
        var second = col.AddOrUpdate("r1", new Dictionary<string, string?> { ["name"] = "Bob" });

        Assert.False(second.IsNew);
        Assert.Equal(first.RowIndex, second.RowIndex);
        Assert.Equal("Bob", col.GetValue(second.RowIndex, col.Schema.GetFieldIndex("name")));
    }

    [Fact]
    public void Delete_ExistingRow_ReturnsMutationAndRemovesRow()
    {
        var col = CreateCollection();
        var row = col.AddOrUpdate("r1", new Dictionary<string, string?> { ["name"] = "Alice" });

        var deleted = col.Delete("r1");

        Assert.NotNull(deleted);
        Assert.Equal("r1", deleted!.RowId);
        Assert.Null(col.GetRowId(row.RowIndex));
    }

    [Fact]
    public void Delete_NonExistingRow_ReturnsNull()
    {
        var col = CreateCollection();
        Assert.Null(col.Delete("missing"));
    }

    [Fact]
    public void GetAllLiveIndexes_ExcludesDeletedRows()
    {
        var col = CreateCollection();
        col.AddOrUpdate("r1", new Dictionary<string, string?> { ["name"] = "Alice" });
        col.AddOrUpdate("r2", new Dictionary<string, string?> { ["name"] = "Bob" });
        col.Delete("r1");

        var live = col.GetAllLiveIndexes();

        Assert.Single(live);
        Assert.Contains(live, pair => pair.Key == "r2");
    }

    [Fact]
    public void GetAllLiveIndexes_ReturnsAllLiveRows()
    {
        var col = CreateCollection();
        col.AddOrUpdate("r1", new Dictionary<string, string?> { ["name"] = "Alice" });
        col.AddOrUpdate("r2", new Dictionary<string, string?> { ["name"] = "Bob" });
        col.Delete("r1");
        col.AddOrUpdate("r3", new Dictionary<string, string?> { ["name"] = "Carol" });

        var live = col.GetAllLiveIndexes();

        Assert.Equal(2, live.Count);
        var ids = live.Select(x => x.Key).ToHashSet();
        Assert.Contains("r2", ids);
        Assert.Contains("r3", ids);
    }

    [Fact]
    public void GetRowValues_ReturnsStoredRowBuffer()
    {
        var col = CreateCollection();
        var row = col.AddOrUpdate("r1", new Dictionary<string, string?> { ["name"] = "Alice", ["score"] = "42" });

        var values = col.GetRowValues(row.RowIndex);

        Assert.Equal("r1", values[0]);
        Assert.Equal("Alice", values[1]);
        Assert.Equal("42", values[2]);
    }
}
