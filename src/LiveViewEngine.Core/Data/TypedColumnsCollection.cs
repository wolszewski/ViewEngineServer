namespace LiveViewEngine.Core.Data;

public sealed class TypedColumnsCollection
{
    private readonly CollectionSchema _schema;
    private readonly bool[] _activatedFields;

    private readonly List<List<int?>> _int32Columns;
    private readonly List<List<long?>> _int64Columns;
    private readonly List<List<double?>> _doubleColumns;
    private readonly List<List<decimal?>> _decimalColumns;
    private readonly List<List<DateOnly?>> _dateOnlyColumns;
    private readonly List<List<DateTime?>> _dateTimeColumns;
    private readonly List<List<DateTimeOffset?>> _dateTimeOffsetColumns;

    public TypedColumnsCollection(CollectionSchema schema)
    {
        _schema = schema;
        _activatedFields = new bool[schema.Fields.Count];

        _int32Columns = new List<List<int?>>();
        _int64Columns = new List<List<long?>>();
        _doubleColumns = new List<List<double?>>();
        _decimalColumns = new List<List<decimal?>>();
        _dateOnlyColumns = new List<List<DateOnly?>>();
        _dateTimeColumns = new List<List<DateTime?>>();
        _dateTimeOffsetColumns = new List<List<DateTimeOffset?>>();
    }

    public bool IsActivated(int fieldIndex) =>
        fieldIndex >= 0 && fieldIndex < _activatedFields.Length && _activatedFields[fieldIndex];

    public void AddRow()
    {
        for (var i = 0; i < _int32Columns.Count; i++) { _int32Columns[i].Add(null); }
        for (var i = 0; i < _int64Columns.Count; i++) { _int64Columns[i].Add(null); }
        for (var i = 0; i < _doubleColumns.Count; i++) { _doubleColumns[i].Add(null); }
        for (var i = 0; i < _decimalColumns.Count; i++) { _decimalColumns[i].Add(null); }
        for (var i = 0; i < _dateOnlyColumns.Count; i++) { _dateOnlyColumns[i].Add(null); }
        for (var i = 0; i < _dateTimeColumns.Count; i++) { _dateTimeColumns[i].Add(null); }
        for (var i = 0; i < _dateTimeOffsetColumns.Count; i++) { _dateTimeOffsetColumns[i].Add(null); }
    }

    public void ClearReusedSlot(int rowIndex)
    {
        for (var i = 0; i < _int32Columns.Count; i++) { _int32Columns[i][rowIndex] = null; }
        for (var i = 0; i < _int64Columns.Count; i++) { _int64Columns[i][rowIndex] = null; }
        for (var i = 0; i < _doubleColumns.Count; i++) { _doubleColumns[i][rowIndex] = null; }
        for (var i = 0; i < _decimalColumns.Count; i++) { _decimalColumns[i][rowIndex] = null; }
        for (var i = 0; i < _dateOnlyColumns.Count; i++) { _dateOnlyColumns[i][rowIndex] = null; }
        for (var i = 0; i < _dateTimeColumns.Count; i++) { _dateTimeColumns[i][rowIndex] = null; }
        for (var i = 0; i < _dateTimeOffsetColumns.Count; i++) { _dateTimeOffsetColumns[i][rowIndex] = null; }
    }

    public int? GetInt32(int fieldIndex, int rowIndex)
    {
        if (!IsActivated(fieldIndex)) { return null; }
        return _int32Columns[_schema.GetFieldDefinition(fieldIndex).TypedColumnIndex][rowIndex];
    }

    public long? GetInt64(int fieldIndex, int rowIndex)
    {
        if (!IsActivated(fieldIndex)) { return null; }
        return _int64Columns[_schema.GetFieldDefinition(fieldIndex).TypedColumnIndex][rowIndex];
    }

    public double? GetDouble(int fieldIndex, int rowIndex)
    {
        if (!IsActivated(fieldIndex)) { return null; }
        return _doubleColumns[_schema.GetFieldDefinition(fieldIndex).TypedColumnIndex][rowIndex];
    }

    public decimal? GetDecimal(int fieldIndex, int rowIndex)
    {
        if (!IsActivated(fieldIndex)) { return null; }
        return _decimalColumns[_schema.GetFieldDefinition(fieldIndex).TypedColumnIndex][rowIndex];
    }

    public DateOnly? GetDateOnly(int fieldIndex, int rowIndex)
    {
        if (!IsActivated(fieldIndex)) { return null; }
        return _dateOnlyColumns[_schema.GetFieldDefinition(fieldIndex).TypedColumnIndex][rowIndex];
    }

    public DateTime? GetDateTime(int fieldIndex, int rowIndex)
    {
        if (!IsActivated(fieldIndex)) { return null; }
        return _dateTimeColumns[_schema.GetFieldDefinition(fieldIndex).TypedColumnIndex][rowIndex];
    }

    public DateTimeOffset? GetDateTimeOffset(int fieldIndex, int rowIndex)
    {
        if (!IsActivated(fieldIndex)) { return null; }
        return _dateTimeOffsetColumns[_schema.GetFieldDefinition(fieldIndex).TypedColumnIndex][rowIndex];
    }

    public void ActivateField(int fieldIndex, ScalarFieldType type, int rowCount, IReadOnlyDictionary<int, string?> rowValues)
    {
        if (IsActivated(fieldIndex))
        {
            return;
        }

        var fieldDefinition = _schema.GetFieldDefinition(fieldIndex);
        if (fieldDefinition.Type != type || fieldDefinition.TypedColumnIndex < 0)
        {
            return;
        }

        switch (type)
        {
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

        var typedColumnIndex = _schema.GetFieldDefinition(fieldIndex).TypedColumnIndex;

        switch (type)
        {
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
