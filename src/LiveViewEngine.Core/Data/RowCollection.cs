namespace LiveViewEngine.Core.Data;

public sealed class RowCollection
{
    private readonly Dictionary<string, int> _rowKeyToIndex = new();
    private readonly SlotList<string?[]> _rows = new();
    private readonly TypedColumnsCollection _typedColumns;

    public RowCollection(CollectionSchema schema)
    {
        Schema = schema;
        _typedColumns = new TypedColumnsCollection(schema);
    }

    public CollectionSchema Schema { get; }

    public MutationInfo AddOrUpdate(string key, IReadOnlyDictionary<string, string?> fieldChanges)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Row key is required.", nameof(key));
        }

        var row = GetOrAddRow(key, out var rowIndex, out var isNew);
        var columnChanges = Schema.MapToColumnChanges(fieldChanges);
        List<KeyValuePair<int, string?>>? normalizedChanges = null;
        var processedCount = 0;

        foreach (var updatedField in columnChanges)
        {
            var fieldDefinition = Schema.GetFieldDefinition(updatedField.Key);
            var normalizedValue = NormalizeValue(fieldDefinition.Type, updatedField.Value);
            row[updatedField.Key] = normalizedValue;
            UpdateTypedValueForField(rowIndex, updatedField.Key, normalizedValue);

            if (normalizedValue != updatedField.Value && normalizedChanges is null)
            {
                normalizedChanges = new List<KeyValuePair<int, string?>>(columnChanges.Count);
                var remaining = processedCount;
                foreach (var prev in columnChanges)
                {
                    if (remaining-- == 0)
                    {
                        break;
                    }

                    normalizedChanges.Add(prev);
                }
            }

            normalizedChanges?.Add(new KeyValuePair<int, string?>(updatedField.Key, normalizedValue));
            processedCount++;
        }

        var changes = normalizedChanges ?? columnChanges;
        return new MutationInfo(key, rowIndex, isNew, changes, FieldMask.From(changes));
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
        }
        else
        {
            _typedColumns.ClearReusedSlot(rowIndex);
        }

        newRow[0] = rowKey;
        _rowKeyToIndex[rowKey] = rowIndex;
        return newRow;
    }

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
        if (fieldDefinition.Type is ScalarFieldType.String or ScalarFieldType.Boolean)
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
        if (fieldDefinition.Type is ScalarFieldType.String or ScalarFieldType.Boolean)
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
        if (source is null)
        {
            throw new InvalidOperationException($"Row at index {index} is deleted.");
        }

        return source;
    }

    private void UpdateTypedValueForField(int rowIndex, int fieldIndex, string? value)
    {
        if (!_typedColumns.IsActivated(fieldIndex))
        {
            return;
        }

        _typedColumns.UpdateFieldValue(fieldIndex, Schema.GetFieldDefinition(fieldIndex).Type, rowIndex, value);
    }

    private static string? NormalizeValue(ScalarFieldType type, string? value)
    {
        if (type == ScalarFieldType.Boolean)
        {
            if (ScalarValueConverter.TryConvertBoolean(value, out var parsed))
            {
                return ScalarValueConverter.FormatBoolean(parsed);
            }
        }

        return value;
    }
}
