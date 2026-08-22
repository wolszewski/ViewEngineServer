using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.UnitTests;

public class CollectionStoreTests
{
    private static CollectionSchema MakeSchema(string id = "col1") =>
        new(id, ["name"]);

    private static CollectionStore MakeStore() => new(new ViewEngineMetrics());

    [Fact]
    public void TryCreate_NewId_ReturnsTrue()
    {
        var store = MakeStore();
        Assert.True(store.TryCreateCollection(MakeSchema()));
    }

    [Fact]
    public void TryCreate_DuplicateId_ReturnsFalse()
    {
        var store = MakeStore();
        store.TryCreateCollection(MakeSchema());
        Assert.False(store.TryCreateCollection(MakeSchema()));
    }

    [Fact]
    public void TryGet_ExistingCollection_ReturnsTrue_AndNonNull()
    {
        var store = MakeStore();
        store.TryCreateCollection(MakeSchema());

        Assert.True(store.TryGet("col1", out var col));
        Assert.NotNull(col);
    }

    [Fact]
    public void TryGet_NonExistentCollection_ReturnsFalse()
    {
        var store = MakeStore();
        Assert.False(store.TryGet("missing", out var col));
        Assert.Null(col);
    }

    [Fact]
    public void CollectionIds_ReflectsCreatedCollections()
    {
        var store = MakeStore();
        store.TryCreateCollection(MakeSchema("a"));
        store.TryCreateCollection(MakeSchema("b"));
        Assert.Contains("a", store.CollectionIds);
        Assert.Contains("b", store.CollectionIds);
        Assert.Equal(2, store.CollectionIds.Count);
    }
}
