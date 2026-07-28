namespace ViewEngineServer.Core;

public sealed record MutationInfo(
    string RowId,
    int Handle,
    bool IsNew,
    object?[]? PreviousValues,
    object?[]? NewValues);

public sealed class ColumnarCollection
{
    private readonly int _capacity;
    private readonly object?[][] _columns;
    private readonly string?[] _handleToRowId;
    private readonly Dictionary<string, int> _rowIdToHandle = new();
    private int _nextHandle;
    private int _liveCount;
    private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.NoRecursion);
    public CollectionSchema Schema { get; }

    public ColumnarCollection(CollectionSchema schema)
    {
        Schema = schema;
        _capacity = schema.Capacity;
        _columns = new object?[schema.Fields.Count][];
        for (int i = 0; i < schema.Fields.Count; i++)
        {
            _columns[i] = new object?[_capacity];
        }

        _handleToRowId = new string?[_capacity];
    }

    public int LiveCount => Volatile.Read(ref _liveCount);


    public MutationInfo Upsert(IReadOnlyDictionary<string, object?> fields)
    {
        var pkName = Schema.PrimaryKeyField.Name;
        if (!fields.TryGetValue(pkName, out var pkRaw) || pkRaw is null)
        {
            throw new ArgumentException($"Primary key field '{pkName}' is required.");
        }

        var rowId = pkRaw.ToString()!;

        _rwLock.EnterWriteLock();
        try
        {
            bool isNew;
            int handle;
            object?[]? previousValues = null;

            if (_rowIdToHandle.TryGetValue(rowId, out handle))
            {
                isNew = false;
                previousValues = new object?[Schema.Fields.Count];
                for (int i = 0; i < Schema.Fields.Count; i++)
                {
                    previousValues[i] = _columns[i][handle];
                }
            }
            else
            {
                if (_nextHandle >= _capacity)
                {
                    throw new InvalidOperationException(
                        $"Collection '{Schema.CollectionId}' is at capacity ({_capacity}). " +
                        "Consider deleting stale rows or increasing the capacity when creating the collection.");
                }

                handle = _nextHandle++;
                isNew = true;
                _liveCount++;
                _rowIdToHandle[rowId] = handle;
                _handleToRowId[handle] = rowId;
            }

            var newValues = new object?[Schema.Fields.Count];
            for (int i = 0; i < Schema.Fields.Count; i++)
            {
                if (fields.TryGetValue(Schema.Fields[i].Name, out var val))
                {
                    _columns[i][handle] = val;
                }

                newValues[i] = _columns[i][handle];
            }

            return new MutationInfo(rowId, handle, isNew, previousValues, newValues);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public MutationInfo? Delete(string rowId)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (!_rowIdToHandle.TryGetValue(rowId, out var handle))
            {
                return null;
            }

            var previousValues = new object?[Schema.Fields.Count];
            for (int i = 0; i < Schema.Fields.Count; i++)
            {
                previousValues[i] = _columns[i][handle];
                _columns[i][handle] = null;
            }

            _rowIdToHandle.Remove(rowId);
            _handleToRowId[handle] = null;
            _liveCount--;

            return new MutationInfo(rowId, handle, false, previousValues, null);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }


    public object? GetValue(int handle, int fieldIndex)
    {
        _rwLock.EnterReadLock();
        try
        {
            return _columns[fieldIndex][handle];
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public bool IsLive(int handle)
    {
        _rwLock.EnterReadLock();
        try
        {
            return handle >= 0 && handle < _nextHandle && _handleToRowId[handle] is not null;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public string? GetRowId(int handle)
    {
        _rwLock.EnterReadLock();
        try
        {
            return handle < _nextHandle ? _handleToRowId[handle] : null;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public bool TryGetHandle(string rowId, out int handle)
    {
        _rwLock.EnterReadLock();
        try
        {
            return _rowIdToHandle.TryGetValue(rowId, out handle);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public IReadOnlyList<(int handle, string rowId)> GetAllLiveHandles()
    {
        _rwLock.EnterReadLock();
        try
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
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public IReadOnlyDictionary<string, object?> GetRow(int handle)
    {
        _rwLock.EnterReadLock();
        try
        {
            var row = new Dictionary<string, object?>(Schema.Fields.Count);
            for (int i = 0; i < Schema.Fields.Count; i++)
            {
                row[Schema.Fields[i].Name] = _columns[i][handle];
            }

            return row;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }
}