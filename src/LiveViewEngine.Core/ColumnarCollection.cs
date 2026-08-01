namespace LiveViewEngine.Core;

public sealed class ColumnarCollection
{
    private readonly List<string?>[] _columns;
    private readonly List<string?> _handleToRowId = new();
    private readonly Dictionary<string, int> _rowIdToHandle = new();
    private int _nextHandle;
    private int _liveCount;
    public CollectionSchema Schema { get; }

    public ColumnarCollection(CollectionSchema schema)
    {
        Schema = schema;
        _columns = new List<string?>[schema.Fields.Count];
        for (int i = 0; i < schema.Fields.Count; i++)
        {
            _columns[i] = [];
        }
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
        int handle;
        string?[]? previousValues = null;

        if (_rowIdToHandle.TryGetValue(rowId, out handle))
        {
            isNew = false;
            previousValues = new string?[Schema.Fields.Count];
            for (int i = 0; i < Schema.Fields.Count; i++)
            {
                previousValues[i] = GetValue(handle, i);
            }
        }
        else
        {
            if (_nextHandle >= Schema.Capacity)
            {
                throw new InvalidOperationException(
                    $"Collection '{Schema.CollectionId}' is at capacity ({Schema.Capacity}). " +
                    "Consider deleting stale rows or increasing the capacity when creating the collection.");
            }

            handle = _nextHandle++;
            isNew = true;
            _liveCount++;
            _rowIdToHandle[rowId] = handle;
            _handleToRowId.Add(rowId);
        }

        var newValues = new string?[Schema.Fields.Count];
        for (int i = 0; i < Schema.Fields.Count; i++)
        {
            if (fields.TryGetValue(Schema.Fields[i].Name, out var val))
            {
                SetValue(handle, i, val);
            }

            newValues[i] = GetValue(handle, i);
        }

        return new MutationInfo(rowId, handle, isNew, previousValues, newValues);
    }

    public MutationInfo? Delete(string rowId)
    {
        if (!_rowIdToHandle.TryGetValue(rowId, out var handle))
        {
            return null;
        }

        var previousValues = new string?[Schema.Fields.Count];
        for (int i = 0; i < Schema.Fields.Count; i++)
        {
            previousValues[i] = GetValue(handle, i);
            SetValue(handle, i, null);
        }

        _rowIdToHandle.Remove(rowId);
        _handleToRowId[handle] = null;
        _liveCount--;

        return new MutationInfo(rowId, handle, false, previousValues, null);
    }

    public string? GetValue(int handle, int fieldIndex)
    {
        var col = _columns[fieldIndex];
        return handle < col.Count ? col[handle] : null;
    }

    public bool IsLive(int handle) =>
        handle >= 0 && handle < _nextHandle && _handleToRowId[handle] is not null;

    public string? GetRowId(int handle) =>
        handle < _nextHandle ? _handleToRowId[handle] : null;

    public bool TryGetHandle(string rowId, out int handle) =>
        _rowIdToHandle.TryGetValue(rowId, out handle);

    public IReadOnlyList<(int handle, string rowId)> GetAllLiveHandles()
    {
        var list = new List<(int, string)>(_liveCount);
        for (int h = 0; h < _nextHandle; h++)
        {
            if (_handleToRowId[h] is { } id)
            {
                list.Add((h, id));
            }
        }
        return list;
    }

    public IReadOnlyDictionary<string, string?> GetRow(int handle)
    {
        var row = new Dictionary<string, string?>(Schema.Fields.Count);
        for (int i = 0; i < Schema.Fields.Count; i++)
        {
            row[Schema.Fields[i].Name] = GetValue(handle, i);
        }
        return row;
    }

    private void SetValue(int handle, int fieldIndex, string? value)
    {
        var col = _columns[fieldIndex];
        while (col.Count <= handle)
        {
            col.Add(null);
        }
        col[handle] = value;
    }
}
