using System.Collections.Frozen;

namespace LiveViewEngine.Core.Data;

public sealed record FieldDefinition(string Name, int FieldIndex);

public sealed class CollectionSchema
{
    private readonly FrozenDictionary<string, int> _fieldNameToIndex;

    public string CollectionName { get; }
    public IReadOnlyList<FieldDefinition> Fields { get; private set; }
    public FieldDefinition PrimaryKey { get; }
    public  const int PrimaryKeyIndex = 0;

    public CollectionSchema(string collectionName, IList<string> fieldNames)
    {
        CollectionName = collectionName;
        var fields = MapFieldDefinitions(fieldNames);
        Fields = fields;
        PrimaryKey = fields[0];
        _fieldNameToIndex = Fields.ToFrozenDictionary(f => f.Name, f => f.FieldIndex);
    }

    private static FieldDefinition[] MapFieldDefinitions(IList<string> fieldNames)
    {
        var fields = new FieldDefinition[fieldNames.Count + 1];
        fields[0] = new FieldDefinition("key", 0);
        for (int i = 1; i <= fieldNames.Count; i++)
        {
            fields[i] = new FieldDefinition(fieldNames[i - 1], i);
        }

        return fields;
    }

    public int GetFieldIndex(string name)
    {
        return _fieldNameToIndex.GetValueOrDefault(name, -1);
    }
    
    public IReadOnlyCollection<KeyValuePair<int, string?>> MapToColumnChanges(
        IReadOnlyDictionary<string, string?> fieldValues)
    {
        if (fieldValues.Count == 0)
        {
            return Array.Empty<KeyValuePair<int, string?>>();
        }

        var mapped = new KeyValuePair<int, string?>[fieldValues.Count];
        var index = 0;
        foreach (var (fieldName, value) in fieldValues)
        {
            if (!_fieldNameToIndex.TryGetValue(fieldName, out var fieldIndex))
            {
                throw new ArgumentException(
                    $"Unknown field '{fieldName}' for collection '{CollectionName}'.",
                    nameof(fieldValues));
            }

            mapped[index++] = new KeyValuePair<int, string?>(fieldIndex, value);
        }

        return mapped;
    }
}