namespace LiveViewEngine.Core.Data;

public sealed class RowCollection(CollectionSchema schema)
{
    private readonly Dictionary<string, int> _rowKeyToIndex = new();
    private readonly SlotList<string?[]> _rows = new();
    private readonly TypedColumnsCollection _typedColumns = new(schema);
    // Stable per-slot arrival sequence, assigned once per true insertion. Unlike _rowKeyToIndex's
    // enumeration order or the SlotList's reused row indexes, this always reflects true insertion
    // order even across delete+reinsert churn, so NaturalOrderIndex can rebuild correct arrival
    // order at construction time instead of relying on incidental dictionary/slot layout.
    private readonly List<long> _arrivalSequence = new();
    private long _nextArrivalSequence;

    public CollectionSchema Schema { get; } = schema;

    public MutationInfo AddOrUpdate(string key, IReadOnlyDictionary<string, string?> fieldChanges)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Row key is required.", nameof(key));
        }

        var row = GetOrAddRow(key, out var rowIndex, out var isNew);
        var columnChanges = Schema.MapToColumnChanges(fieldChanges);

        foreach (var updatedField in columnChanges)
        {
            row[updatedField.Key] = updatedField.Value;
            UpdateTypedValueForField(rowIndex, updatedField.Key, updatedField.Value);
        }

        return new MutationInfo(key, rowIndex, isNew, columnChanges, FieldMask.From(columnChanges));
    }

    private string?[] GetOrAddRow(string rowKey, out int rowIndex, out bool isNew)
    {
        isNew = !_rowKeyToIndex.TryGetValue(rowKey, out rowIndex);

        if (!isNew)
        {
            return _rows[rowIndex]!;
        }

        var newRow = new string?[Schema.Fields.Count];
        var previousCapacity = _rows.Capacity;

        rowIndex = _rows.Add(newRow);

        if (rowIndex >= previousCapacity)
        {
            _typedColumns.AddRow();
            _arrivalSequence.Add(_nextArrivalSequence++);
        }
        else
        {
            _typedColumns.ClearReusedSlot(rowIndex);
            _arrivalSequence[rowIndex] = _nextArrivalSequence++;
        }

        newRow[0] = rowKey;
        _rowKeyToIndex[rowKey] = rowIndex;
        return newRow;
    }

    // True insertion-order sequence for the row currently occupying rowIndex — stable even if this
    // slot was previously freed and reused by an unrelated later insertion.
    public long GetArrivalSequence(int rowIndex) => _arrivalSequence[rowIndex];

    public MutationInfo? Delete(string rowId)
    {
        if (!_rowKeyToIndex.TryGetValue(rowId, out var index))
        {
            return null;
        }

        var row = _rows[index];
        if (row is null)
        {
            return null;
        }

        _rowKeyToIndex.Remove(rowId);
        _rows.RemoveAt(index);

        return new MutationInfo(rowId, index, false, null, default);
    }

    public string? GetValue(int index, int fieldIndex)
    {
        var row = _rows[index];
        return row?[fieldIndex];
    }

    public bool? GetBoolean(int rowIndex, int fieldIndex) => _typedColumns.GetBoolean(fieldIndex, rowIndex);

    public int? GetInt32(int rowIndex, int fieldIndex) => _typedColumns.GetInt32(fieldIndex, rowIndex);

    public long? GetInt64(int rowIndex, int fieldIndex) => _typedColumns.GetInt64(fieldIndex, rowIndex);

    public double? GetDouble(int rowIndex, int fieldIndex) => _typedColumns.GetDouble(fieldIndex, rowIndex);

    public decimal? GetDecimal(int rowIndex, int fieldIndex) => _typedColumns.GetDecimal(fieldIndex, rowIndex);

    public DateOnly? GetDateOnly(int rowIndex, int fieldIndex) => _typedColumns.GetDateOnly(fieldIndex, rowIndex);

    public DateTime? GetDateTime(int rowIndex, int fieldIndex) => _typedColumns.GetDateTime(fieldIndex, rowIndex);

    public DateTimeOffset? GetDateTimeOffset(int rowIndex, int fieldIndex) => _typedColumns.GetDateTimeOffset(fieldIndex, rowIndex);

    public void ActivateTypedField(int fieldIndex)
    {
        if (fieldIndex < 0 || fieldIndex >= Schema.Fields.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldIndex));
        }

        if (_typedColumns.IsActivated(fieldIndex))
        {
            return;
        }

        var fieldDefinition = Schema.GetFieldDefinition(fieldIndex);
        if (fieldDefinition.Type is ScalarFieldType.String)
        {
            return;
        }

        var rowValues = new Dictionary<int, string?>();
        foreach (var pair in _rowKeyToIndex)
        {
            rowValues[pair.Value] = GetValue(pair.Value, fieldIndex);
        }

        _typedColumns.ActivateField(fieldIndex, fieldDefinition.Type, _rows.Capacity, rowValues);
    }

    public void AddTypedFieldRef(int fieldIndex)
    {
        var fieldDefinition = Schema.GetFieldDefinition(fieldIndex);
        if (fieldDefinition.Type is ScalarFieldType.String)
        {
            return;
        }

        var rowValues = new Dictionary<int, string?>();
        foreach (var pair in _rowKeyToIndex)
        {
            rowValues[pair.Value] = GetValue(pair.Value, fieldIndex);
        }

        _typedColumns.AddRef(fieldIndex, fieldDefinition.Type, _rows.Capacity, rowValues);
    }

    public void ReleaseTypedFieldRef(int fieldIndex) => _typedColumns.ReleaseRef(fieldIndex);

    public bool IsTypedFieldActivated(int fieldIndex) => _typedColumns.IsActivated(fieldIndex);

    public IEnumerable<(int FieldIndex, DateTime FlaggedAt)> GetPendingTypedColumnDeactivations() =>
        _typedColumns.GetPendingDeactivations();

    public void TryDeactivatePendingTypedColumn(int fieldIndex) => _typedColumns.TryDeactivatePending(fieldIndex);

    public int GetTypedFieldRefCount(int fieldIndex) => _typedColumns.GetRefCount(fieldIndex);

    public IEnumerable<(string FieldName, int RefCount)> GetActiveTypedColumns()
    {
        foreach (var (fieldIndex, refCount) in _typedColumns.GetActiveColumns())
        {
            yield return (Schema.GetFieldDefinition(fieldIndex).Name, refCount);
        }
    }

    public string? GetRowId(int index) => _rows[index]?[CollectionSchema.PrimaryKeyIndex];

    public ICollection<KeyValuePair<string, int>> GetAllLiveIndexes() => _rowKeyToIndex;

    public bool TryGetRowIndex(string key, out int rowIndex) =>
        _rowKeyToIndex.TryGetValue(key, out rowIndex);

    public string?[] GetRowValues(int index)
    {
        var source = _rows[index];
        return source ?? throw new InvalidOperationException($"Row at index {index} is deleted.");
    }

    private void UpdateTypedValueForField(int rowIndex, int fieldIndex, string? value)
    {
        if (!_typedColumns.IsActivated(fieldIndex))
        {
            return;
        }

        _typedColumns.UpdateFieldValue(fieldIndex, Schema.GetFieldDefinition(fieldIndex).Type, rowIndex, value);
    }
}
