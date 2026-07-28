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

    private sealed class Int32Column : IColumn
    {
        private readonly List<int?> _data = [];
        public string? GetStringValue(int handle) => handle < _data.Count ? _data[handle]?.ToString(CultureInfo.InvariantCulture) : null;
        public object? GetTypedValue(int handle) => handle < _data.Count ? _data[handle] : null;
        public void SetValue(int handle, object? value)
        {
            while (_data.Count <= handle) { _data.Add(null); }
            _data[handle] = value is null ? null : Convert.ToInt32(value);
        }
        public void Clear(int handle) { if (handle < _data.Count) { _data[handle] = null; } }
    }

    private sealed class Int64Column : IColumn
    {
        private readonly List<long?> _data = [];
        public string? GetStringValue(int handle) => handle < _data.Count ? _data[handle]?.ToString(CultureInfo.InvariantCulture) : null;
        public object? GetTypedValue(int handle) => handle < _data.Count ? _data[handle] : null;
        public void SetValue(int handle, object? value)
        {
            while (_data.Count <= handle) { _data.Add(null); }
            _data[handle] = value is null ? null : Convert.ToInt64(value);
        }
        public void Clear(int handle) { if (handle < _data.Count) { _data[handle] = null; } }
    }

    private sealed class DecimalColumn : IColumn
    {
        private readonly List<decimal?> _data = [];
        public string? GetStringValue(int handle) => handle < _data.Count ? _data[handle]?.ToString(CultureInfo.InvariantCulture) : null;
        public object? GetTypedValue(int handle) => handle < _data.Count ? _data[handle] : null;
        public void SetValue(int handle, object? value)
        {
            while (_data.Count <= handle) { _data.Add(null); }
            _data[handle] = value is null ? null : Convert.ToDecimal(value);
        }
        public void Clear(int handle) { if (handle < _data.Count) { _data[handle] = null; } }
    }

    private sealed class StringColumn : IColumn
    {
        private readonly List<string?> _data = [];
        public string? GetStringValue(int handle) => handle < _data.Count ? _data[handle] : null;
        public object? GetTypedValue(int handle) => handle < _data.Count ? _data[handle] : null;
        public void SetValue(int handle, object? value)
        {
            while (_data.Count <= handle) { _data.Add(null); }
            _data[handle] = value?.ToString();
        }
        public void Clear(int handle) { if (handle < _data.Count) { _data[handle] = null; } }
    }

    private sealed class BoolColumn : IColumn
    {
        private readonly List<bool?> _data = [];
        public string? GetStringValue(int handle) => handle < _data.Count && _data[handle] is { } v ? (v ? "true" : "false") : null;
        public object? GetTypedValue(int handle) => handle < _data.Count ? _data[handle] : null;
        public void SetValue(int handle, object? value)
        {
            while (_data.Count <= handle) { _data.Add(null); }
            _data[handle] = value is null ? null : Convert.ToBoolean(value);
        }
        public void Clear(int handle) { if (handle < _data.Count) { _data[handle] = null; } }
    }

    private sealed class DateTimeColumn : IColumn
    {
        private readonly List<DateTime?> _data = [];
        public string? GetStringValue(int handle) => handle < _data.Count ? _data[handle]?.ToString("O", CultureInfo.InvariantCulture) : null;
        public object? GetTypedValue(int handle) => handle < _data.Count ? _data[handle] : null;
        public void SetValue(int handle, object? value)
        {
            while (_data.Count <= handle) { _data.Add(null); }
            _data[handle] = value is null ? null :
                value is DateTime dt ? dt :
                DateTime.Parse(value.ToString()!, CultureInfo.InvariantCulture);
        }
        public void Clear(int handle) { if (handle < _data.Count) { _data[handle] = null; } }
    }

    private sealed class DateOnlyColumn : IColumn
    {
        private readonly List<DateOnly?> _data = [];
        public string? GetStringValue(int handle) => handle < _data.Count ? _data[handle]?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
        public object? GetTypedValue(int handle) => handle < _data.Count ? _data[handle] : null;
        public void SetValue(int handle, object? value)
        {
            while (_data.Count <= handle) { _data.Add(null); }
            _data[handle] = value is null ? null :
                value is DateOnly d ? d :
                DateOnly.Parse(value.ToString()!, CultureInfo.InvariantCulture);
        }
        public void Clear(int handle) { if (handle < _data.Count) { _data[handle] = null; } }
    }

    private sealed class ByteColumn : IColumn
    {
        private readonly List<byte?> _data = [];
        public string? GetStringValue(int handle) => handle < _data.Count ? _data[handle]?.ToString(CultureInfo.InvariantCulture) : null;
        public object? GetTypedValue(int handle) => handle < _data.Count ? _data[handle] : null;
        public void SetValue(int handle, object? value)
        {
            while (_data.Count <= handle) { _data.Add(null); }
            _data[handle] = value is null ? null : Convert.ToByte(value);
        }
        public void Clear(int handle) { if (handle < _data.Count) { _data[handle] = null; } }
    }

    private readonly IColumn[] _columns;
    private readonly List<string?> _handleToRowId = new();
    private readonly Dictionary<string, int> _rowIdToHandle = new();
    private int _nextHandle;
    private int _liveCount;
    private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.NoRecursion);
    public CollectionSchema Schema { get; }

    public ColumnarCollection(CollectionSchema schema)
    {
        Schema = schema;
        _columns = new IColumn[schema.Fields.Count];
        for (int i = 0; i < schema.Fields.Count; i++)
        {
            _columns[i] = schema.Fields[i].Type switch
            {
                FieldType.Int32 => new Int32Column(),
                FieldType.Int64 => new Int64Column(),
                FieldType.Decimal => new DecimalColumn(),
                FieldType.String => new StringColumn(),
                FieldType.Boolean => new BoolColumn(),
                FieldType.DateTime => new DateTimeColumn(),
                FieldType.DateOnly => new DateOnlyColumn(),
                FieldType.Byte => new ByteColumn(),
                _ => throw new ArgumentOutOfRangeException(nameof(schema),
                    $"Unsupported field type '{schema.Fields[i].Type}' for field '{schema.Fields[i].Name}'.")
            };
        }
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
