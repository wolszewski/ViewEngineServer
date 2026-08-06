namespace LiveViewEngine.Core.Data;

public sealed class RowCollection(CollectionSchema schema)
{
    private readonly List<string?[]?> _rows = new();
    private readonly Dictionary<string, int> _rowKeyToIndex = new();
    private readonly Stack<int> _freeIndexes = new();
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
        }

        return new MutationInfo(key, rowIndex, isNew, columnChanges);
    }

    private string?[] GetOrAddRow(string rowKey, out int rowIndex, out bool isNew)
    {
        isNew = !_rowKeyToIndex.TryGetValue(rowKey, out rowIndex);

        if (!isNew)
        {
            return _rows[rowIndex]!;
        }
        
        var newRow = new string?[Schema.Fields.Count];

        if (_freeIndexes.Count > 0)
        {
            rowIndex = _freeIndexes.Pop();
            _rows[rowIndex] = newRow;
        }
        else
        {
            rowIndex = _rows.Count;
            _rows.Add(newRow);
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
        _rows[index] = null;
        _freeIndexes.Push(index);

        return new MutationInfo(rowId, index, false, null);
    }

    public string? GetValue(int index, int fieldIndex)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return null;
        }

        var row = _rows[index];
        return row?[fieldIndex];
    }

    public string? GetRowId(int index)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return null;
        }

        return _rows[index]?[CollectionSchema.PrimaryKeyIndex];
    }

    public ICollection<KeyValuePair<string, int>> GetAllLiveIndexes()
    {
        return _rowKeyToIndex;
    }

    public string?[] GetRowValues(int index)
    {
        var source = _rows[index];
        return source ?? throw new InvalidOperationException($"Row at index {index} is deleted.");
    }
}