using ViewEngineServer.Core.Schema;

namespace ViewEngineServer.Core.Storage;

// ---------------------------------------------------------------------------
// Mutation result — returned by Upsert / Delete so callers can compute deltas
// ---------------------------------------------------------------------------

/// <param name="RowId">String representation of the primary-key value.</param>
/// <param name="Handle">Stable integer slot for this row (never reused).</param>
/// <param name="IsNew">True if this was an insert; false if an update.</param>
/// <param name="PreviousValues">Field values before mutation (null when IsNew).</param>
/// <param name="NewValues">Field values after mutation (null when deleted).</param>
public sealed record MutationInfo(
    string RowId,
    int Handle,
    bool IsNew,
    object?[]? PreviousValues,
    object?[]? NewValues);

// ---------------------------------------------------------------------------
// Columnar collection
// ---------------------------------------------------------------------------

/// <summary>
/// Fixed-capacity in-memory store with one primitive array per field.
/// Rows are addressed by a monotonically-assigned integer <em>handle</em>.
/// Deleted handles are never reused or compacted so that sort indexes remain
/// valid without requiring a full rebuild after every delete.
/// </summary>
public sealed class ColumnarCollection
{
    public CollectionSchema Schema { get; }

    private readonly int _capacity;

    // _columns[fieldIndex][handle] — one array per field
    private readonly object?[][] _columns;

    // Slot-to-id mapping; null means the slot is deleted/unused
    private readonly string?[] _handleToRowId;

    // Reverse: rowId → handle
    private readonly Dictionary<string, int> _rowIdToHandle = new();

    private int _nextHandle;
    private int _liveCount;

    private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.NoRecursion);

    public ColumnarCollection(CollectionSchema schema)
    {
        Schema = schema;
        _capacity = schema.Capacity;
        _columns = new object?[schema.Fields.Count][];
        for (int i = 0; i < schema.Fields.Count; i++)
            _columns[i] = new object?[_capacity];
        _handleToRowId = new string?[_capacity];
    }

    public int LiveCount => Volatile.Read(ref _liveCount);

    // -----------------------------------------------------------------------
    // Mutations (write lock)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Insert or update a row. Returns metadata including previous field values
    /// so that callers can propagate deltas without re-reading the store.
    /// </summary>
    public MutationInfo Upsert(IReadOnlyDictionary<string, object?> fields)
    {
        var pkName = Schema.PrimaryKeyField.Name;
        if (!fields.TryGetValue(pkName, out var pkRaw) || pkRaw is null)
            throw new ArgumentException($"Primary key field '{pkName}' is required.");

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
                    previousValues[i] = _columns[i][handle];
            }
            else
            {
                if (_nextHandle >= _capacity)
                    throw new InvalidOperationException(
                        $"Collection '{Schema.CollectionId}' is at capacity ({_capacity}). " +
                        "Consider deleting stale rows or increasing the capacity when creating the collection.");
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
                    _columns[i][handle] = val;
                newValues[i] = _columns[i][handle];
            }

            return new MutationInfo(rowId, handle, isNew, previousValues, newValues);
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Logically delete a row. Returns null if the row does not exist.
    /// The handle is invalidated but the slot is not reused.
    /// </summary>
    public MutationInfo? Delete(string rowId)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (!_rowIdToHandle.TryGetValue(rowId, out var handle)) return null;

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
        finally { _rwLock.ExitWriteLock(); }
    }

    // -----------------------------------------------------------------------
    // Reads (read lock)
    // -----------------------------------------------------------------------

    public object? GetValue(int handle, int fieldIndex)
    {
        _rwLock.EnterReadLock();
        try { return _columns[fieldIndex][handle]; }
        finally { _rwLock.ExitReadLock(); }
    }

    public bool IsLive(int handle)
    {
        _rwLock.EnterReadLock();
        try { return handle >= 0 && handle < _nextHandle && _handleToRowId[handle] is not null; }
        finally { _rwLock.ExitReadLock(); }
    }

    public string? GetRowId(int handle)
    {
        _rwLock.EnterReadLock();
        try { return handle < _nextHandle ? _handleToRowId[handle] : null; }
        finally { _rwLock.ExitReadLock(); }
    }

    public bool TryGetHandle(string rowId, out int handle)
    {
        _rwLock.EnterReadLock();
        try { return _rowIdToHandle.TryGetValue(rowId, out handle); }
        finally { _rwLock.ExitReadLock(); }
    }

    /// <summary>Returns a snapshot of all (handle, rowId) pairs for live rows.</summary>
    public IReadOnlyList<(int handle, string rowId)> GetAllLiveHandles()
    {
        _rwLock.EnterReadLock();
        try
        {
            var list = new List<(int, string)>(_liveCount);
            for (int h = 0; h < _nextHandle; h++)
                if (_handleToRowId[h] is { } id) list.Add((h, id));
            return list;
        }
        finally { _rwLock.ExitReadLock(); }
    }

    /// <summary>Returns all field values for a live handle as a dictionary.</summary>
    public IReadOnlyDictionary<string, object?> GetRow(int handle)
    {
        _rwLock.EnterReadLock();
        try
        {
            var row = new Dictionary<string, object?>(Schema.Fields.Count);
            for (int i = 0; i < Schema.Fields.Count; i++)
                row[Schema.Fields[i].Name] = _columns[i][handle];
            return row;
        }
        finally { _rwLock.ExitReadLock(); }
    }
}
