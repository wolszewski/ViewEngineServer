namespace LiveViewEngine.Core;

public sealed class RowCollection
{
    private readonly List<string?[]> _rows;
    private int _liveCount;
    public CollectionSchema Schema { get; }

    public RowCollection(CollectionSchema schema)
    {
        Schema = schema;
        _rows = new List<string?[]>(schema.Fields.Count);
    }

    public int LiveCount => _liveCount;

    public MutationInfo Upsert(IReadOnlyDictionary<string, string?> fields)
    {
        var pkName = Schema.PrimaryKey.Name;
        if (!fields.TryGetValue(pkName, out var rowId) || rowId is null)
        {
            throw new ArgumentException($"Primary key field '{pkName}' is required.");
        }

        bool isNew;
        int index;
        string?[]? previousValues = null;

        if (Schema.TryGetIndex(rowId, out index))
        {
            isNew = false;
            previousValues = (string?[])_rows[index].Clone();
        }
        else
        {
            index = Schema.AddRowId(rowId);
            isNew = true;
            _liveCount++;
            _rows.Add(new string?[Schema.Fields.Count]);
        }

        var row = _rows[index];
        for (int i = 0; i < Schema.Fields.Count; i++)
        {
            if (fields.TryGetValue(Schema.Fields[i].Name, out var val))
            {
                row[i] = val;
            }
        }

        var newValues = (string?[])row.Clone();
        return new MutationInfo(rowId, index, isNew, previousValues, newValues);
    }

    public MutationInfo? Delete(string rowId)
    {
        if (!Schema.TryGetIndex(rowId, out var index))
        {
            return null;
        }

        var row = _rows[index];
        var previousValues = (string?[])row.Clone();
        for (int i = 0; i < Schema.Fields.Count; i++)
        {
            row[i] = null;
        }

        Schema.RemoveRowId(rowId, index);
        _liveCount--;

        return new MutationInfo(rowId, index, false, previousValues, null);
    }

    public string? GetValue(int index, int fieldIndex)
    {
        return index >= 0 && index < _rows.Count ? _rows[index][fieldIndex] : null;
    }

    public bool IsLive(int index) => Schema.IsLiveIndex(index);

    public string? GetRowId(int index) => Schema.GetRowId(index);

    public bool TryGetIndex(string rowId, out int index) => Schema.TryGetIndex(rowId, out index);

    public IReadOnlyList<(int index, string rowId)> GetAllLiveIndexes() => Schema.GetAllLiveIndexes();

    public IReadOnlyDictionary<string, string?> GetRow(int index)
    {
        var row = new Dictionary<string, string?>(Schema.Fields.Count);
        for (int i = 0; i < Schema.Fields.Count; i++)
        {
            row[Schema.Fields[i].Name] = GetValue(index, i);
        }
        return row;
    }

}
