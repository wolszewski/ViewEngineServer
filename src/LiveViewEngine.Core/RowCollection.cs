namespace LiveViewEngine.Core;

public sealed class RowCollection
{
    private readonly List<string?[]> _rows;
    private readonly List<string?> _indexToRowId = new();
    private readonly Dictionary<string, int> _rowIdToIndex = new();
    private int _nextIndex;
    private int _liveCount;
    public CollectionSchema Schema { get; }

    public RowCollection(CollectionSchema schema)
    {
        Schema = schema;
        _rows = new List<string?[]>(schema.Fields.Count);
    }

    public int LiveCount => _liveCount;

    public MutationInfo Upsert(IReadOnlyDictionary<string, string?> fields)
    {
        var pkName = Schema.PrimaryKeyField.Name;
        if (!fields.TryGetValue(pkName, out var rowId) || rowId is null)
        {
            throw new ArgumentException($"Primary key field '{pkName}' is required.");
        }

        bool isNew;
        int index;
        string?[]? previousValues = null;

        if (_rowIdToIndex.TryGetValue(rowId, out index))
        {
            isNew = false;
            previousValues = (string?[])_rows[index].Clone();
        }
        else
        {
            if (_nextIndex >= Schema.Capacity)
            {
                throw new InvalidOperationException(
                    $"Collection '{Schema.CollectionId}' is at capacity ({Schema.Capacity}). " +
                    "Consider deleting stale rows or increasing the capacity when creating the collection.");
            }

            index = _nextIndex++;
            isNew = true;
            _liveCount++;
            _rowIdToIndex[rowId] = index;
            _indexToRowId.Add(rowId);
            _rows.Add(new string?[Schema.Fields.Count]);
        }

        var row = _rows[index];
        for (int i = 0; i < Schema.Fields.Count; i++)
        {
            if (fields.TryGetValue(Schema.Fields[i].Name, out var val))
            {
                row[i] = val;
            }
        }

        var newValues = (string?[])row.Clone();
        return new MutationInfo(rowId, index, isNew, previousValues, newValues);
    }

    public MutationInfo? Delete(string rowId)
    {
        if (!_rowIdToIndex.TryGetValue(rowId, out var index))
        {
            return null;
        }

        var row = _rows[index];
        var previousValues = (string?[])row.Clone();
        for (int i = 0; i < Schema.Fields.Count; i++)
        {
            row[i] = null;
        }

        _rowIdToIndex.Remove(rowId);
        _indexToRowId[index] = null;
        _liveCount--;

        return new MutationInfo(rowId, index, false, previousValues, null);
    }

    public string? GetValue(int index, int fieldIndex)
    {
        return index >= 0 && index < _nextIndex ? _rows[index][fieldIndex] : null;
    }

    public bool IsLive(int index) =>
        index >= 0 && index < _nextIndex && _indexToRowId[index] is not null;

    public string? GetRowId(int index) =>
        index < _nextIndex ? _indexToRowId[index] : null;

    public bool TryGetIndex(string rowId, out int index) =>
        _rowIdToIndex.TryGetValue(rowId, out index);

    public IReadOnlyList<(int index, string rowId)> GetAllLiveIndexes()
    {
        var list = new List<(int, string)>(_liveCount);
        for (int i = 0; i < _nextIndex; i++)
        {
            if (_indexToRowId[i] is { } id)
            {
                list.Add((i, id));
            }
        }
        return list;
    }

    public IReadOnlyDictionary<string, string?> GetRow(int index)
    {
        var row = new Dictionary<string, string?>(Schema.Fields.Count);
        for (int i = 0; i < Schema.Fields.Count; i++)
        {
            row[Schema.Fields[i].Name] = GetValue(index, i);
        }
        return row;
    }

}
