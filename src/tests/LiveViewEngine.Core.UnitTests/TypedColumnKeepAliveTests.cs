using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.UnitTests;

public class TypedColumnKeepAliveTests
{
    private static RowCollection MakeCollection()
    {
        var schema = new CollectionSchema("test", ["score", "name"], [ScalarFieldType.Int32, ScalarFieldType.String]);
        return new RowCollection(schema);
    }

    [Fact]
    public void WhenReferencedByIndexes_FilterDoesNotActivateTypedColumn()
    {
        var col = MakeCollection();
        col.AddOrUpdate("a", new Dictionary<string, string?> { ["score"] = "42" });
        var schema = col.Schema;
        var scoreFieldIndex = schema.GetFieldIndex("score");

        var filter = new FilterSpec("score", FilterOperator.Gt, "10");
        var filterSet = FilterSet.Create([filter], schema, col, TypedColumnKeepAlive.WhenReferencedByIndexes);

        Assert.False(col.IsTypedFieldActivated(scoreFieldIndex));
    }

    [Fact]
    public void WhenReferencedByIndexes_FilterFallsBackToRawParse_AndMatchesCorrectly()
    {
        var col = MakeCollection();
        col.AddOrUpdate("a", new Dictionary<string, string?> { ["score"] = "5" });
        col.AddOrUpdate("b", new Dictionary<string, string?> { ["score"] = "15" });
        col.AddOrUpdate("c", new Dictionary<string, string?> { ["score"] = "25" });
        var schema = col.Schema;
        var scoreFieldIndex = schema.GetFieldIndex("score");

        var filter = new FilterSpec("score", FilterOperator.Gt, "10");
        var filterSet = FilterSet.Create([filter], schema, col, TypedColumnKeepAlive.WhenReferencedByIndexes);

        col.TryGetRowIndex("a", out var aIdx);
        col.TryGetRowIndex("b", out var bIdx);
        col.TryGetRowIndex("c", out var cIdx);

        Assert.False(filterSet.Passes(col, aIdx));
        Assert.True(filterSet.Passes(col, bIdx));
        Assert.True(filterSet.Passes(col, cIdx));
    }

    [Fact]
    public void WhenReferencedByIndexesAndFilters_FilterActivatesTypedColumn()
    {
        var col = MakeCollection();
        col.AddOrUpdate("a", new Dictionary<string, string?> { ["score"] = "42" });
        var schema = col.Schema;
        var scoreFieldIndex = schema.GetFieldIndex("score");

        var filter = new FilterSpec("score", FilterOperator.Gt, "10");
        var filterSet = FilterSet.Create([filter], schema, col, TypedColumnKeepAlive.WhenReferencedByIndexesAndFilters);

        Assert.True(col.IsTypedFieldActivated(scoreFieldIndex));
    }

    [Fact]
    public void WhenReferencedByIndexesAndFilters_DisposeReleasesRef_AndDeactivatesColumn()
    {
        var col = MakeCollection();
        col.AddOrUpdate("a", new Dictionary<string, string?> { ["score"] = "42" });
        var schema = col.Schema;
        var scoreFieldIndex = schema.GetFieldIndex("score");

        var filterSet = FilterSet.Create(
            [new FilterSpec("score", FilterOperator.Gt, "10")],
            schema, col, TypedColumnKeepAlive.WhenReferencedByIndexesAndFilters);

        Assert.True(col.IsTypedFieldActivated(scoreFieldIndex));

        filterSet.Dispose();

        Assert.Equal(0, col.GetTypedFieldRefCount(scoreFieldIndex));
    }

    [Fact]
    public void WhenReferencedByIndexesAndFilters_TwoFiltersOnSameField_ColumnStaysActiveUntilBothDisposed()
    {
        var col = MakeCollection();
        col.AddOrUpdate("a", new Dictionary<string, string?> { ["score"] = "42" });
        var schema = col.Schema;
        var scoreFieldIndex = schema.GetFieldIndex("score");

        var fs1 = FilterSet.Create(
            [new FilterSpec("score", FilterOperator.Gt, "10")],
            schema, col, TypedColumnKeepAlive.WhenReferencedByIndexesAndFilters);
        var fs2 = FilterSet.Create(
            [new FilterSpec("score", FilterOperator.Lt, "100")],
            schema, col, TypedColumnKeepAlive.WhenReferencedByIndexesAndFilters);

        fs1.Dispose();
        Assert.Equal(1, col.GetTypedFieldRefCount(scoreFieldIndex)); // still held by fs2

        fs2.Dispose();
        Assert.Equal(0, col.GetTypedFieldRefCount(scoreFieldIndex)); // now free, pending deactivation
    }

    [Fact]
    public void SortIndex_ActivatesTypedColumn_ViaRefCount()
    {
        var col = MakeCollection();
        col.AddOrUpdate("a", new Dictionary<string, string?> { ["score"] = "42" });
        var schema = col.Schema;
        var scoreFieldIndex = schema.GetFieldIndex("score");

        Assert.False(col.IsTypedFieldActivated(scoreFieldIndex));

        var idx = new SortIndex(col, scoreFieldIndex);

        Assert.True(col.IsTypedFieldActivated(scoreFieldIndex));
    }

    [Fact]
    public void WhenReferencedByIndexes_FilterUsesTypedColumnIfActivatedBySortIndex()
    {
        var col = MakeCollection();
        col.AddOrUpdate("a", new Dictionary<string, string?> { ["score"] = "5" });
        col.AddOrUpdate("b", new Dictionary<string, string?> { ["score"] = "15" });
        var schema = col.Schema;
        var scoreFieldIndex = schema.GetFieldIndex("score");

        var idx = new SortIndex(col, scoreFieldIndex); // activates typed column via AddRef
        Assert.True(col.IsTypedFieldActivated(scoreFieldIndex));

        var filter = new FilterSpec("score", FilterOperator.Gt, "10");
        var filterSet = FilterSet.Create([filter], schema, col, TypedColumnKeepAlive.WhenReferencedByIndexes);

        col.TryGetRowIndex("a", out var aIdx);
        col.TryGetRowIndex("b", out var bIdx);

        Assert.False(filterSet.Passes(col, aIdx));
        Assert.True(filterSet.Passes(col, bIdx));
    }
}
