namespace LiveViewEngine.Core;

public sealed record FieldDefinition(string Name, int Index);

public sealed class CollectionSchema
{
    private readonly List<string?> _indexToRowId = new();
    private readonly Dictionary<string, int> _rowIdToIndex = new();
    public string CollectionName { get; private set; }
    public IReadOnlyList<FieldDefinition> Fields { get; private set; }
    public FieldDefinition PrimaryKey { get; private set; }
    
    public CollectionSchema(string collectionName, IList<FieldDefinition> fields, string primaryKey)
    {
        CollectionName = collectionName;
        Fields = fields.ToList().AsReadOnly();
        PrimaryKey = Fields.First(f => f.Name == primaryKey);
    }

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

    public bool TryGetIndex(string rowId, out int index)
    {
        return _rowIdToIndex.TryGetValue(rowId, out index);
    }

    public int AddRowId(string rowId)
    {
        var index = _indexToRowId.Count;
        _indexToRowId.Add(rowId);
        _rowIdToIndex[rowId] = index;
        return index;
    }

    public void RemoveRowId(string rowId, int index)
    {
        _rowIdToIndex.Remove(rowId);
        _indexToRowId[index] = null;
    }

    public string? GetRowId(int index)
    {
        return index >= 0 && index < _indexToRowId.Count ? _indexToRowId[index] : null;
    }

    public bool IsLiveIndex(int index)
    {
        return index >= 0 && index < _indexToRowId.Count && _indexToRowId[index] is not null;
    }

    public IReadOnlyList<(int index, string rowId)> GetAllLiveIndexes()
    {
        var list = new List<(int, string)>(_rowIdToIndex.Count);
        for (int i = 0; i < _indexToRowId.Count; i++)
        {
            if (_indexToRowId[i] is { } rowId)
            {
                list.Add((i, rowId));
            }
        }
        return list;
    }
}
