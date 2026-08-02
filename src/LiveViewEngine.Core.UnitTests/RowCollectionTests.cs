namespace LiveViewEngine.Core.UnitTests;

public class RowCollectionTests
{
    private static RowCollection CreateCollection(int capacity = 100) =>
        new(new CollectionSchema
        {
            CollectionId = "test",
            Capacity = capacity,
            Fields =
            [
                new FieldDefinition("id", FieldType.String, IsPrimaryKey: true),
                new FieldDefinition("name", FieldType.String, IsSortable: true),
                new FieldDefinition("score", FieldType.String)
            ]
        });


    [Fact]
    public void Upsert_NewRow_ReturnsIsNewTrue()
    {
        var col = CreateCollection();
        var result = col.Upsert(new Dictionary<string, string?> { ["id"] = "r1", ["name"] = "Alice", ["score"] = "10" });

        Assert.True(result.IsNew);
        Assert.Equal("r1", result.RowId);
        Assert.Null(result.PreviousValues);
        Assert.NotNull(result.NewValues);
    }

    [Fact]
    public void Upsert_NewRow_IncreasesLiveCount()
    {
        var col = CreateCollection();
        col.Upsert(new Dictionary<string, string?> { ["id"] = "r1" });
        Assert.Equal(1, col.LiveCount);
    }

    [Fact]
    public void Upsert_MultipleRows_AssignsUniqueIndexes()
    {
        var col = CreateCollection();
        var r1 = col.Upsert(new Dictionary<string, string?> { ["id"] = "r1" });
        var r2 = col.Upsert(new Dictionary<string, string?> { ["id"] = "r2" });
        Assert.NotEqual(r1.Index, r2.Index);
    }


    [Fact]
    public void Upsert_ExistingRow_ReturnsIsNewFalse_WithPreviousValues()
    {
        var col = CreateCollection();
        col.Upsert(new Dictionary<string, string?> { ["id"] = "r1", ["name"] = "Alice" });
        var result = col.Upsert(new Dictionary<string, string?> { ["id"] = "r1", ["name"] = "Bob" });

        Assert.False(result.IsNew);
        Assert.Equal("Alice", result.PreviousValues?[1]);
        Assert.Equal("Bob", result.NewValues?[1]);
    }

    [Fact]
    public void Upsert_ExistingRow_DoesNotIncreaseLiveCount()
    {
        var col = CreateCollection();
        col.Upsert(new Dictionary<string, string?> { ["id"] = "r1", ["name"] = "Alice" });
        col.Upsert(new Dictionary<string, string?> { ["id"] = "r1", ["name"] = "Bob" });
        Assert.Equal(1, col.LiveCount);
    }

    [Fact]
    public void Upsert_MissingPrimaryKey_Throws()
    {
        var col = CreateCollection();
        Assert.Throws<ArgumentException>(() =>
            col.Upsert(new Dictionary<string, string?> { ["name"] = "Alice" }));
    }

    [Fact]
    public void Upsert_AtCapacity_Throws()
    {
        var col = CreateCollection(capacity: 1);
        col.Upsert(new Dictionary<string, string?> { ["id"] = "r1" });
        Assert.Throws<InvalidOperationException>(() =>
            col.Upsert(new Dictionary<string, string?> { ["id"] = "r2" }));
    }


    [Fact]
    public void Delete_ExistingRow_ReturnsNonNull_AndDecrementsLiveCount()
    {
        var col = CreateCollection();
        col.Upsert(new Dictionary<string, string?> { ["id"] = "r1", ["name"] = "Alice" });
        var result = col.Delete("r1");

        Assert.NotNull(result);
        Assert.Equal("r1", result.RowId);
        Assert.Equal("Alice", result.PreviousValues?[1]);
        Assert.Null(result.NewValues);
        Assert.Equal(0, col.LiveCount);
    }

    [Fact]
    public void Delete_NonExistentRow_ReturnsNull()
    {
        var col = CreateCollection();
        Assert.Null(col.Delete("ghost"));
    }

    [Fact]
    public void Delete_ThenGetRowId_ReturnsNull()
    {
        var col = CreateCollection();
        var r = col.Upsert(new Dictionary<string, string?> { ["id"] = "r1" });
        col.Delete("r1");
        Assert.Null(col.GetRowId(r.Index));
    }


    [Fact]
    public void GetRow_ReturnsAllFields()
    {
        var col = CreateCollection();
        var r = col.Upsert(new Dictionary<string, string?> { ["id"] = "r1", ["name"] = "Alice", ["score"] = "42" });
        var row = col.GetRow(r.Index);

        Assert.Equal("r1", row["id"]);
        Assert.Equal("Alice", row["name"]);
        Assert.Equal("42", row["score"]);
    }

    [Fact]
    public void IsLive_ReturnsTrueForInsertedRow()
    {
        var col = CreateCollection();
        var r = col.Upsert(new Dictionary<string, string?> { ["id"] = "r1" });
        Assert.True(col.IsLive(r.Index));
    }

    [Fact]
    public void IsLive_ReturnsFalseAfterDelete()
    {
        var col = CreateCollection();
        var r = col.Upsert(new Dictionary<string, string?> { ["id"] = "r1" });
        col.Delete("r1");
        Assert.False(col.IsLive(r.Index));
    }


    [Fact]
    public void GetAllLiveIndexes_ReturnsOnlyLiveRows()
    {
        var col = CreateCollection();
        col.Upsert(new Dictionary<string, string?> { ["id"] = "r1" });
        col.Upsert(new Dictionary<string, string?> { ["id"] = "r2" });
        col.Delete("r1");

        var live = col.GetAllLiveIndexes();
        Assert.Single(live);
        Assert.Equal("r2", live[0].rowId);
    }


    [Fact]
    public void TryGetIndex_FindsInsertedRow()
    {
        var col = CreateCollection();
        var r = col.Upsert(new Dictionary<string, string?> { ["id"] = "r1" });
        Assert.True(col.TryGetIndex("r1", out var index));
        Assert.Equal(r.Index, index);
    }

    [Fact]
    public void TryGetIndex_ReturnsFalseAfterDelete()
    {
        var col = CreateCollection();
        col.Upsert(new Dictionary<string, string?> { ["id"] = "r1" });
        col.Delete("r1");
        Assert.False(col.TryGetIndex("r1", out _));
    }
}
