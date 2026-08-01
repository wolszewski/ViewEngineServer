namespace ViewEngineServer.WebApp.Core;

public enum FieldType { String }

public sealed record FieldDefinition(
    string Name,
    FieldType Type,
    bool IsPrimaryKey = false,
    bool IsSortable = false,
    bool IsFilterable = false);

public sealed class CollectionSchema
{
    public required string CollectionId { get; init; }

    public int Capacity { get; init; } = 100_000;

    public required IReadOnlyList<FieldDefinition> Fields { get; init; }

    private int _pkIndex = -2;

    public int PrimaryKeyIndex
    {
        get
        {
            if (_pkIndex >= -1)
            {
                return _pkIndex;
            }

            for (int i = 0; i < Fields.Count; i++)
            {
                if (Fields[i].IsPrimaryKey) { _pkIndex = i; return i; }
            }

            _pkIndex = -1;
            throw new InvalidOperationException(
                $"Collection '{CollectionId}' has no field marked IsPrimaryKey.");
        }
    }

    public FieldDefinition PrimaryKeyField => Fields[PrimaryKeyIndex];

    public int GetFieldIndex(string name)
    {
        for (int i = 0; i < Fields.Count; i++)
        {
            if (Fields[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }
}
