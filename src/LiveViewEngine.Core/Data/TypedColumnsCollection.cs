namespace LiveViewEngine.Core.Data;

public sealed class TypedColumnsCollection(CollectionSchema schema)
{
    private readonly bool[] _activatedFields = new bool[schema.Fields.Count];
    private readonly int[] _refCounts = new int[schema.Fields.Count];
    private readonly Dictionary<int, DateTime> _pendingDeactivation = [];

    private readonly List<List<bool?>> _booleanColumns = [];
    private readonly List<List<int?>> _int32Columns = [];
    private readonly List<List<long?>> _int64Columns = [];
    private readonly List<List<double?>> _doubleColumns = [];
    private readonly List<List<decimal?>> _decimalColumns = [];
    private readonly List<List<DateOnly?>> _dateOnlyColumns = [];
    private readonly List<List<DateTime?>> _dateTimeColumns = [];
    private readonly List<List<DateTimeOffset?>> _dateTimeOffsetColumns = [];

    public bool IsActivated(int fieldIndex) =>
        fieldIndex >= 0 && fieldIndex < _activatedFields.Length && _activatedFields[fieldIndex];

    public int GetRefCount(int fieldIndex) =>
        fieldIndex >= 0 && fieldIndex < _refCounts.Length ? _refCounts[fieldIndex] : 0;

    public IEnumerable<(int FieldIndex, int RefCount)> GetActiveColumns()
    {
        for (var i = 0; i < _activatedFields.Length; i++)
        {
            if (_activatedFields[i])
            {
                yield return (i, _refCounts[i]);
            }
        }
    }

    public void AddRef(int fieldIndex, ScalarFieldType type, int rowCount, IReadOnlyDictionary<int, string?> rowValues)
    {
        _pendingDeactivation.Remove(fieldIndex);
        _refCounts[fieldIndex]++;
        ActivateField(fieldIndex, type, rowCount, rowValues);
    }

    public void ReleaseRef(int fieldIndex)
    {
        if (_refCounts[fieldIndex] <= 0 || !IsActivated(fieldIndex))
        {
            return;
        }

        if (--_refCounts[fieldIndex] == 0)
        {
            _pendingDeactivation.TryAdd(fieldIndex, DateTime.UtcNow);
        }
    }

    public IEnumerable<(int FieldIndex, DateTime FlaggedAt)> GetPendingDeactivations()
    {
        foreach (var kv in _pendingDeactivation)
        {
            yield return (kv.Key, kv.Value);
        }
    }

    public void TryDeactivatePending(int fieldIndex)
    {
        if (_refCounts[fieldIndex] == 0 && _pendingDeactivation.ContainsKey(fieldIndex))
        {
            _pendingDeactivation.Remove(fieldIndex);
            DeactivateField(fieldIndex);
        }
    }

    private void DeactivateField(int fieldIndex)
    {
        var fieldDefinition = schema.GetFieldDefinition(fieldIndex);
        var typedColumnIndex = fieldDefinition.TypedColumnIndex;

        switch (fieldDefinition.Type)
        {
            case ScalarFieldType.Boolean:
                if (typedColumnIndex < _booleanColumns.Count) { _booleanColumns[typedColumnIndex] = null!; }
                break;
            case ScalarFieldType.Int32:
                if (typedColumnIndex < _int32Columns.Count) { _int32Columns[typedColumnIndex] = null!; }
                break;
            case ScalarFieldType.Int64:
                if (typedColumnIndex < _int64Columns.Count) { _int64Columns[typedColumnIndex] = null!; }
                break;
            case ScalarFieldType.Double:
                if (typedColumnIndex < _doubleColumns.Count) { _doubleColumns[typedColumnIndex] = null!; }
                break;
            case ScalarFieldType.Decimal:
                if (typedColumnIndex < _decimalColumns.Count) { _decimalColumns[typedColumnIndex] = null!; }
                break;
            case ScalarFieldType.DateOnly:
                if (typedColumnIndex < _dateOnlyColumns.Count) { _dateOnlyColumns[typedColumnIndex] = null!; }
                break;
            case ScalarFieldType.DateTime:
                if (typedColumnIndex < _dateTimeColumns.Count) { _dateTimeColumns[typedColumnIndex] = null!; }
                break;
            case ScalarFieldType.DateTimeOffset:
                if (typedColumnIndex < _dateTimeOffsetColumns.Count) { _dateTimeOffsetColumns[typedColumnIndex] = null!; }
                break;
        }

        _activatedFields[fieldIndex] = false;
    }

    public void AddRow()
    {
        for (var i = 0; i < _booleanColumns.Count; i++) { _booleanColumns[i]?.Add(null); }
        for (var i = 0; i < _int32Columns.Count; i++) { _int32Columns[i]?.Add(null); }
        for (var i = 0; i < _int64Columns.Count; i++) { _int64Columns[i]?.Add(null); }
        for (var i = 0; i < _doubleColumns.Count; i++) { _doubleColumns[i]?.Add(null); }
        for (var i = 0; i < _decimalColumns.Count; i++) { _decimalColumns[i]?.Add(null); }
        for (var i = 0; i < _dateOnlyColumns.Count; i++) { _dateOnlyColumns[i]?.Add(null); }
        for (var i = 0; i < _dateTimeColumns.Count; i++) { _dateTimeColumns[i]?.Add(null); }
        for (var i = 0; i < _dateTimeOffsetColumns.Count; i++) { _dateTimeOffsetColumns[i]?.Add(null); }
    }

    public void ClearReusedSlot(int rowIndex)
    {
        for (var i = 0; i < _booleanColumns.Count; i++) { if (_booleanColumns[i] is { } c) { c[rowIndex] = null; } }
        for (var i = 0; i < _int32Columns.Count; i++) { if (_int32Columns[i] is { } c) { c[rowIndex] = null; } }
        for (var i = 0; i < _int64Columns.Count; i++) { if (_int64Columns[i] is { } c) { c[rowIndex] = null; } }
        for (var i = 0; i < _doubleColumns.Count; i++) { if (_doubleColumns[i] is { } c) { c[rowIndex] = null; } }
        for (var i = 0; i < _decimalColumns.Count; i++) { if (_decimalColumns[i] is { } c) { c[rowIndex] = null; } }
        for (var i = 0; i < _dateOnlyColumns.Count; i++) { if (_dateOnlyColumns[i] is { } c) { c[rowIndex] = null; } }
        for (var i = 0; i < _dateTimeColumns.Count; i++) { if (_dateTimeColumns[i] is { } c) { c[rowIndex] = null; } }
        for (var i = 0; i < _dateTimeOffsetColumns.Count; i++) { if (_dateTimeOffsetColumns[i] is { } c) { c[rowIndex] = null; } }
    }

    public int? GetInt32(int fieldIndex, int rowIndex)
    {
        if (!IsActivated(fieldIndex)) { return null; }
        return _int32Columns[schema.GetFieldDefinition(fieldIndex).TypedColumnIndex][rowIndex];
    }

    public bool? GetBoolean(int fieldIndex, int rowIndex)
    {
        if (!IsActivated(fieldIndex)) { return null; }
        return _booleanColumns[schema.GetFieldDefinition(fieldIndex).TypedColumnIndex][rowIndex];
    }

    public long? GetInt64(int fieldIndex, int rowIndex)
    {
        if (!IsActivated(fieldIndex)) { return null; }
        return _int64Columns[schema.GetFieldDefinition(fieldIndex).TypedColumnIndex][rowIndex];
    }

    public double? GetDouble(int fieldIndex, int rowIndex)
    {
        if (!IsActivated(fieldIndex)) { return null; }
        return _doubleColumns[schema.GetFieldDefinition(fieldIndex).TypedColumnIndex][rowIndex];
    }

    public decimal? GetDecimal(int fieldIndex, int rowIndex)
    {
        if (!IsActivated(fieldIndex)) { return null; }
        return _decimalColumns[schema.GetFieldDefinition(fieldIndex).TypedColumnIndex][rowIndex];
    }

    public DateOnly? GetDateOnly(int fieldIndex, int rowIndex)
    {
        if (!IsActivated(fieldIndex)) { return null; }
        return _dateOnlyColumns[schema.GetFieldDefinition(fieldIndex).TypedColumnIndex][rowIndex];
    }

    public DateTime? GetDateTime(int fieldIndex, int rowIndex)
    {
        if (!IsActivated(fieldIndex)) { return null; }
        return _dateTimeColumns[schema.GetFieldDefinition(fieldIndex).TypedColumnIndex][rowIndex];
    }

    public DateTimeOffset? GetDateTimeOffset(int fieldIndex, int rowIndex)
    {
        if (!IsActivated(fieldIndex)) { return null; }
        return _dateTimeOffsetColumns[schema.GetFieldDefinition(fieldIndex).TypedColumnIndex][rowIndex];
    }

    public void ActivateField(int fieldIndex, ScalarFieldType type, int rowCount, IReadOnlyDictionary<int, string?> rowValues)
    {
        if (IsActivated(fieldIndex))
        {
            return;
        }

        var fieldDefinition = schema.GetFieldDefinition(fieldIndex);
        if (fieldDefinition.Type != type || fieldDefinition.TypedColumnIndex < 0)
        {
            return;
        }

        switch (type)
        {
            case ScalarFieldType.Boolean:
                while (_booleanColumns.Count <= fieldDefinition.TypedColumnIndex) { _booleanColumns.Add(new List<bool?>()); }
                var booleanValues = new List<bool?>(rowCount);
                for (var i = 0; i < rowCount; i++)
                {
                    booleanValues.Add(rowValues.TryGetValue(i, out var raw) && ScalarValueConverter.TryConvertBoolean(raw, out var v) ? v : null);
                }
                _booleanColumns[fieldDefinition.TypedColumnIndex] = booleanValues;
                break;
            case ScalarFieldType.Int32:
                while (_int32Columns.Count <= fieldDefinition.TypedColumnIndex) { _int32Columns.Add(new List<int?>()); }
                var int32Values = new List<int?>(rowCount);
                for (var i = 0; i < rowCount; i++)
                {
                    int32Values.Add(rowValues.TryGetValue(i, out var raw) && ScalarValueConverter.TryConvertInt32(raw, out var v) ? v : null);
                }
                _int32Columns[fieldDefinition.TypedColumnIndex] = int32Values;
                break;
            case ScalarFieldType.Int64:
                while (_int64Columns.Count <= fieldDefinition.TypedColumnIndex) { _int64Columns.Add(new List<long?>()); }
                var int64Values = new List<long?>(rowCount);
                for (var i = 0; i < rowCount; i++)
                {
                    int64Values.Add(rowValues.TryGetValue(i, out var raw) && ScalarValueConverter.TryConvertInt64(raw, out var v) ? v : null);
                }
                _int64Columns[fieldDefinition.TypedColumnIndex] = int64Values;
                break;
            case ScalarFieldType.Double:
                while (_doubleColumns.Count <= fieldDefinition.TypedColumnIndex) { _doubleColumns.Add(new List<double?>()); }
                var doubleValues = new List<double?>(rowCount);
                for (var i = 0; i < rowCount; i++)
                {
                    doubleValues.Add(rowValues.TryGetValue(i, out var raw) && ScalarValueConverter.TryConvertDouble(raw, out var v) ? v : null);
                }
                _doubleColumns[fieldDefinition.TypedColumnIndex] = doubleValues;
                break;
            case ScalarFieldType.Decimal:
                while (_decimalColumns.Count <= fieldDefinition.TypedColumnIndex) { _decimalColumns.Add(new List<decimal?>()); }
                var decimalValues = new List<decimal?>(rowCount);
                for (var i = 0; i < rowCount; i++)
                {
                    decimalValues.Add(rowValues.TryGetValue(i, out var raw) && ScalarValueConverter.TryConvertDecimal(raw, out var v) ? v : null);
                }
                _decimalColumns[fieldDefinition.TypedColumnIndex] = decimalValues;
                break;
            case ScalarFieldType.DateOnly:
                while (_dateOnlyColumns.Count <= fieldDefinition.TypedColumnIndex) { _dateOnlyColumns.Add(new List<DateOnly?>()); }
                var dateOnlyValues = new List<DateOnly?>(rowCount);
                for (var i = 0; i < rowCount; i++)
                {
                    dateOnlyValues.Add(rowValues.TryGetValue(i, out var raw) && ScalarValueConverter.TryConvertDateOnly(raw, out var v) ? v : null);
                }
                _dateOnlyColumns[fieldDefinition.TypedColumnIndex] = dateOnlyValues;
                break;
            case ScalarFieldType.DateTime:
                while (_dateTimeColumns.Count <= fieldDefinition.TypedColumnIndex) { _dateTimeColumns.Add(new List<DateTime?>()); }
                var dateTimeValues = new List<DateTime?>(rowCount);
                for (var i = 0; i < rowCount; i++)
                {
                    dateTimeValues.Add(rowValues.TryGetValue(i, out var raw) && ScalarValueConverter.TryConvertDateTime(raw, out var v) ? v : null);
                }
                _dateTimeColumns[fieldDefinition.TypedColumnIndex] = dateTimeValues;
                break;
            case ScalarFieldType.DateTimeOffset:
                while (_dateTimeOffsetColumns.Count <= fieldDefinition.TypedColumnIndex) { _dateTimeOffsetColumns.Add(new List<DateTimeOffset?>()); }
                var dateTimeOffsetValues = new List<DateTimeOffset?>(rowCount);
                for (var i = 0; i < rowCount; i++)
                {
                    dateTimeOffsetValues.Add(rowValues.TryGetValue(i, out var raw) && ScalarValueConverter.TryConvertDateTimeOffset(raw, out var v) ? v : null);
                }
                _dateTimeOffsetColumns[fieldDefinition.TypedColumnIndex] = dateTimeOffsetValues;
                break;
            case ScalarFieldType.String:
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported scalar field type.");
        }

        _activatedFields[fieldIndex] = true;
    }

    public void UpdateFieldValue(int fieldIndex, ScalarFieldType type, int rowIndex, string? rawValue)
    {
        if (!IsActivated(fieldIndex))
        {
            return;
        }

        var typedColumnIndex = schema.GetFieldDefinition(fieldIndex).TypedColumnIndex;

        switch (type)
        {
            case ScalarFieldType.Boolean:
                _booleanColumns[typedColumnIndex][rowIndex] = ScalarValueConverter.TryConvertBoolean(rawValue, out var convertedBoolean)
                    ? convertedBoolean : null;
                break;
            case ScalarFieldType.Int32:
                _int32Columns[typedColumnIndex][rowIndex] = ScalarValueConverter.TryConvertInt32(rawValue, out var convertedInt32)
                    ? convertedInt32 : null;
                break;
            case ScalarFieldType.Int64:
                _int64Columns[typedColumnIndex][rowIndex] = ScalarValueConverter.TryConvertInt64(rawValue, out var convertedInt64)
                    ? convertedInt64 : null;
                break;
            case ScalarFieldType.Double:
                _doubleColumns[typedColumnIndex][rowIndex] = ScalarValueConverter.TryConvertDouble(rawValue, out var convertedDouble)
                    ? convertedDouble : null;
                break;
            case ScalarFieldType.Decimal:
                _decimalColumns[typedColumnIndex][rowIndex] = ScalarValueConverter.TryConvertDecimal(rawValue, out var convertedDecimal)
                    ? convertedDecimal : null;
                break;
            case ScalarFieldType.DateOnly:
                _dateOnlyColumns[typedColumnIndex][rowIndex] = ScalarValueConverter.TryConvertDateOnly(rawValue, out var convertedDateOnly)
                    ? convertedDateOnly : null;
                break;
            case ScalarFieldType.DateTime:
                _dateTimeColumns[typedColumnIndex][rowIndex] = ScalarValueConverter.TryConvertDateTime(rawValue, out var convertedDateTime)
                    ? convertedDateTime : null;
                break;
            case ScalarFieldType.DateTimeOffset:
                _dateTimeOffsetColumns[typedColumnIndex][rowIndex] = ScalarValueConverter.TryConvertDateTimeOffset(rawValue, out var convertedDateTimeOffset)
                    ? convertedDateTimeOffset : null;
                break;
        }
    }
}
