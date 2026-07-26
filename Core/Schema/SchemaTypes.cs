namespace ViewEngineServer.Core.Schema;

public enum FieldType { Int32, Int64, Double, String, Boolean }

/// <summary>
/// Describes a single field within a collection schema.
/// </summary>
public sealed record FieldDefinition(
    string Name,
    FieldType Type,
    bool IsPrimaryKey = false,
    bool IsSortable = false,
    bool IsFilterable = false);

/// <summary>
/// Immutable schema for a named collection. Defines field layout and capacity.
/// </summary>
public sealed class CollectionSchema
{
    public required string CollectionId { get; init; }

    /// <summary>Maximum number of rows the collection can hold.</summary>
    public int Capacity { get; init; } = 100_000;

    public required IReadOnlyList<FieldDefinition> Fields { get; init; }

    private int _pkIndex = -2; // -2 = not yet resolved

    public int PrimaryKeyIndex
    {
        get
        {
            if (_pkIndex >= -1) return _pkIndex;
            for (int i = 0; i < Fields.Count; i++)
                if (Fields[i].IsPrimaryKey) { _pkIndex = i; return i; }
            _pkIndex = -1;
            throw new InvalidOperationException(
                $"Collection '{CollectionId}' has no field marked IsPrimaryKey.");
        }
    }

    public FieldDefinition PrimaryKeyField => Fields[PrimaryKeyIndex];

    /// <summary>Returns the zero-based index of the named field, or -1 if not found.</summary>
    public int GetFieldIndex(string name)
    {
        for (int i = 0; i < Fields.Count; i++)
            if (Fields[i].Name == name) return i;
        return -1;
    }
}
