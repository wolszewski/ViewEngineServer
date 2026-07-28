using System.Globalization;

namespace ViewEngineServer.Core;

public sealed record MutationInfo(
    string RowId,
    int Handle,
    bool IsNew,
    string?[]? PreviousValues,
    string?[]? NewValues);

public sealed class ColumnarCollection
{
    private interface IColumn
    {
        string? GetStringValue(int handle);
        object? GetTypedValue(int handle);
        void SetValue(int handle, object? value);
        void Clear(int handle);
    }

    private sealed class Int32Column(int capacity) : IColumn
    {
        private readonly int?[] _data = new int?[capacity];
        public string? GetStringValue(int handle) => _data[handle]?.ToString(CultureInfo.InvariantCulture);
        public object? GetTypedValue(int handle) => _data[handle];
        public void SetValue(int handle, object? value) => _data[handle] = value is null ? null : Convert.ToInt32(value);
        public void Clear(int handle) => _data[handle] = null;
    }

    private sealed class Int64Column(int capacity) : IColumn
    {
        private readonly long?[] _data = new long?[capacity];
        public string? GetStringValue(int handle) => _data[handle]?.ToString(CultureInfo.InvariantCulture);
        public object? GetTypedValue(int handle) => _data[handle];
        public void SetValue(int handle, object? value) => _data[handle] = value is null ? null : Convert.ToInt64(value);
        public void Clear(int handle) => _data[handle] = null;
    }

    private sealed class DecimalColumn(int capacity) : IColumn
    {
        private readonly decimal?[] _data = new decimal?[capacity];
        public string? GetStringValue(int handle) => _data[handle]?.ToString(CultureInfo.InvariantCulture);
        public object? GetTypedValue(int handle) => _data[handle];
        public void SetValue(int handle, object? value) => _data[handle] = value is null ? null : Convert.ToDecimal(value);
        public void Clear(int handle) => _data[handle] = null;
    }

    private sealed class StringColumn(int capacity) : IColumn
    {
        private readonly string?[] _data = new string?[capacity];
        public string? GetStringValue(int handle) => _data[handle];
        public object? GetTypedValue(int handle) => _data[handle];
        public void SetValue(int handle, object? value) => _data[handle] = value?.ToString();
        public void Clear(int handle) => _data[handle] = null;
    }

    private sealed class BoolColumn(int capacity) : IColumn
    {
        private readonly bool?[] _data = new bool?[capacity];
        public string? GetStringValue(int handle) => _data[handle] is { } v ? (v ? "true" : "false") : null;
        public object? GetTypedValue(int handle) => _data[handle];
        public void SetValue(int handle, object? value) => _data[handle] = value is null ? null : Convert.ToBoolean(value);
        public void Clear(int handle) => _data[handle] = null;
    }

    private sealed class DateTimeColumn(int capacity) : IColumn
    {
        private readonly DateTime?[] _data = new DateTime?[capacity];
        public string? GetStringValue(int handle) => _data[handle]?.ToString("O", CultureInfo.InvariantCulture);
        public object? GetTypedValue(int handle) => _data[handle];
        public void SetValue(int handle, object? value) =>
            _data[handle] = value is null ? null :
            value is DateTime dt ? dt :
            DateTime.Parse(value.ToString()!, CultureInfo.InvariantCulture);
        public void Clear(int handle) => _data[handle] = null;
    }

    private sealed class DateOnlyColumn(int capacity) : IColumn
    {
        private readonly DateOnly?[] _data = new DateOnly?[capacity];
        public string? GetStringValue(int handle) => _data[handle]?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        public object? GetTypedValue(int handle) => _data[handle];
        public void SetValue(int handle, object? value) =>
            _data[handle] = value is null ? null :
            value is DateOnly d ? d :
            DateOnly.Parse(value.ToString()!, CultureInfo.InvariantCulture);
        public void Clear(int handle) => _data[handle] = null;
    }

    private sealed class ByteColumn(int capacity) : IColumn
    {
        private readonly byte?[] _data = new byte?[capacity];
        public string? GetStringValue(int handle) => _data[handle]?.ToString(CultureInfo.InvariantCulture);
        public object? GetTypedValue(int handle) => _data[handle];
        public void SetValue(int handle, object? value) => _data[handle] = value is null ? null : Convert.ToByte(value);
        public void Clear(int handle) => _data[handle] = null;
    }

    private readonly int _capacity;
    private readonly IColumn[] _columns;
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
        _columns = new IColumn[schema.Fields.Count];
        for (int i = 0; i < schema.Fields.Count; i++)
        {
            _columns[i] = schema.Fields[i].Type switch
            {
                FieldType.Int32 => new Int32Column(_capacity),
                FieldType.Int64 => new Int64Column(_capacity),
                FieldType.Decimal => new DecimalColumn(_capacity),
                FieldType.String => new StringColumn(_capacity),
                FieldType.Boolean => new BoolColumn(_capacity),
                FieldType.DateTime => new DateTimeColumn(_capacity),
                FieldType.DateOnly => new DateOnlyColumn(_capacity),
                FieldType.Byte => new ByteColumn(_capacity),
                _ => throw new ArgumentOutOfRangeException(nameof(schema),
                    $"Unsupported field type '{schema.Fields[i].Type}' for field '{schema.Fields[i].Name}'.")
            };
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
            string?[]? previousValues = null;

            if (_rowIdToHandle.TryGetValue(rowId, out handle))
            {
                isNew = false;
                previousValues = new string?[Schema.Fields.Count];
                for (int i = 0; i < Schema.Fields.Count; i++)
                {
                    previousValues[i] = _columns[i].GetStringValue(handle);
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

            var newValues = new string?[Schema.Fields.Count];
            for (int i = 0; i < Schema.Fields.Count; i++)
            {
                if (fields.TryGetValue(Schema.Fields[i].Name, out var val))
                {
                    _columns[i].SetValue(handle, val);
                }

                newValues[i] = _columns[i].GetStringValue(handle);
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

            var previousValues = new string?[Schema.Fields.Count];
            for (int i = 0; i < Schema.Fields.Count; i++)
            {
                previousValues[i] = _columns[i].GetStringValue(handle);
                _columns[i].Clear(handle);
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


    public string? GetValue(int handle, int fieldIndex)
    {
        _rwLock.EnterReadLock();
        try
        {
            return _columns[fieldIndex].GetStringValue(handle);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    internal object? GetTypedValue(int handle, int fieldIndex)
    {
        _rwLock.EnterReadLock();
        try
        {
            return _columns[fieldIndex].GetTypedValue(handle);
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

    public IReadOnlyDictionary<string, string?> GetRow(int handle)
    {
        _rwLock.EnterReadLock();
        try
        {
            var row = new Dictionary<string, string?>(Schema.Fields.Count);
            for (int i = 0; i < Schema.Fields.Count; i++)
            {
                row[Schema.Fields[i].Name] = _columns[i].GetStringValue(handle);
            }

            return row;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }
}
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
        _columns = new IColumn[schema.Fields.Count];
        for (int i = 0; i < schema.Fields.Count; i++)
        {
            _columns[i] = schema.Fields[i].Type switch
            {
                FieldType.Int32 => new Int32Column(_capacity),
                FieldType.Int64 => new Int64Column(_capacity),
                FieldType.Double => new DoubleColumn(_capacity),
                FieldType.String => new StringColumn(_capacity),
                FieldType.Boolean => new BoolColumn(_capacity),
                _ => throw new ArgumentOutOfRangeException(nameof(schema),
                    $"Unsupported field type '{schema.Fields[i].Type}' for field '{schema.Fields[i].Name}'.")
            };
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
                    previousValues[i] = _columns[i].GetValue(handle);
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
                    _columns[i].SetValue(handle, val);
                }

                newValues[i] = _columns[i].GetValue(handle);
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
                previousValues[i] = _columns[i].GetValue(handle);
                _columns[i].Clear(handle);
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
            return _columns[fieldIndex].GetValue(handle);
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
                row[Schema.Fields[i].Name] = _columns[i].GetValue(handle);
            }

            return row;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }
}