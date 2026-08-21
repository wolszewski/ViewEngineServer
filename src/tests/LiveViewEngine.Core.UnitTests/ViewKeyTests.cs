using LiveViewEngine.Core.Views;

namespace LiveViewEngine.Core.UnitTests;

public class ViewKeyTests
{
    [Fact]
    public void Equals_SameParameters_ReturnsTrue()
    {
        var a = new ViewKey("orders", null, "price", true, null);
        var b = new ViewKey("orders", null, "price", true, null);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equals_DifferentCollection_ReturnsFalse()
    {
        var a = new ViewKey("orders", null, "price", true, null);
        var b = new ViewKey("products", null, "price", true, null);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_DifferentSortColumn_ReturnsFalse()
    {
        var a = new ViewKey("orders", null, "price", true, null);
        var b = new ViewKey("orders", null, "amount", true, null);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_DifferentSortDirection_ReturnsFalse()
    {
        var a = new ViewKey("orders", null, "price", true, null);
        var b = new ViewKey("orders", null, "price", false, null);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_DifferentFilters_ReturnsFalse()
    {
        var fa = new List<FilterSpec> { new("status", FilterOperator.Eq, "open") };
        var fb = new List<FilterSpec> { new("status", FilterOperator.Eq, "closed") };
        var a = new ViewKey("orders", null, null, true, fa);
        var b = new ViewKey("orders", null, null, true, fb);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void GetHashCode_EqualKeys_ReturnSameHash()
    {
        var a = new ViewKey("orders", null, "price", true, null);
        var b = new ViewKey("orders", null, "price", true, null);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Id_ContainsCollectionId()
    {
        var key = new ViewKey("orders", null, null, true, null);
        Assert.Contains("orders", key.Id);
    }

    [Fact]
    public void From_ViewDefinition_ProducesEquivalentKey()
    {
        var def = new ViewDefinition
        {
            CollectionId = "orders",
            SortColumn = "price",
            SortAscending = false,
            Filters = [new FilterSpec("status", FilterOperator.Eq, "open")]
        };
        var key = ViewKey.From(def);

        Assert.Equal("orders", key.CollectionId);
        Assert.Equal("price", key.SortColumn);
        Assert.False(key.SortAscending);
        Assert.Single(key.Filters);
    }

    [Fact]
    public void CanBeUsedAsDictionaryKey()
    {
        var dict = new Dictionary<ViewKey, string>();
        var key1 = new ViewKey("c", null, "f", true, null);
        var key2 = new ViewKey("c", null, "f", true, null);
        dict[key1] = "value";
        Assert.Equal("value", dict[key2]);
    }

    [Fact]
    public void Equals_DifferentSegmentId_ReturnsFalse()
    {
        var a = new ViewKey("orders", "segment-a", null, true, null);
        var b = new ViewKey("orders", "segment-b", null, true, null);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_NullVsNonNullSegmentId_ReturnsFalse()
    {
        var a = new ViewKey("orders", null, null, true, null);
        var b = new ViewKey("orders", "segment-a", null, true, null);
        Assert.NotEqual(a, b);
    }
}
