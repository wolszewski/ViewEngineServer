using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.UnitTests;

public class CollectionStoreTests
{
    private static CollectionSchema MakeSchema(string id = "col1") =>
        new()
        {
            CollectionName = id,
            Fields = [new FieldDefinition("id", FieldType.String, IsPrimaryKey: true)]
        };

    [Fact]
    public void TryCreate_NewId_ReturnsTrue()
    {
        var store = new CollectionStore();
        Assert.True(store.TryCreate(MakeSchema()));
    }

    [Fact]
    public void TryCreate_DuplicateId_ReturnsFalse()
    {
        var store = new CollectionStore();
        store.TryCreate(MakeSchema());
        Assert.False(store.TryCreate(MakeSchema()));
    }

    [Fact]
    public void TryGet_ExistingCollection_ReturnsTrue_AndNonNull()
    {
        var store = new CollectionStore();
        store.TryCreate(MakeSchema());
        Assert.True(store.TryGet("col1", out var col));
        Assert.NotNull(col);
    }

    [Fact]
    public void TryGet_NonExistentCollection_ReturnsFalse()
    {
        var store = new CollectionStore();
        Assert.False(store.TryGet("missing", out var col));
        Assert.Null(col);
    }

    [Fact]
    public void CollectionIds_ReflectsCreatedCollections()
    {
        var store = new CollectionStore();
        store.TryCreate(MakeSchema("a"));
        store.TryCreate(MakeSchema("b"));
        Assert.Contains("a", store.CollectionIds);
        Assert.Contains("b", store.CollectionIds);
        Assert.Equal(2, store.CollectionIds.Count);
    }
}
