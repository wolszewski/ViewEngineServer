using System.Collections.Frozen;

namespace LiveViewEngine.Core.Data;

public enum ScalarFieldType
{
    String,
    Int32,
    Int64,
    Double,
    Decimal,
    DateOnly,
    DateTime,
    DateTimeOffset
}

public sealed record FieldDefinition(
    string Name,
    int FieldIndex,
    ScalarFieldType Type = ScalarFieldType.String,
    int TypedColumnIndex = -1);

public sealed class CollectionSchema
{
    private readonly FrozenDictionary<string, int> _fieldNameToIndex;

    public string CollectionName { get; }
    public IReadOnlyList<FieldDefinition> Fields { get; private set; }
    public FieldDefinition PrimaryKey { get; }
    public const int PrimaryKeyIndex = 0;

    public CollectionSchema(string collectionName, IList<string> fieldNames, IList<ScalarFieldType>? fieldTypes = null)
    {
        CollectionName = collectionName;
        var resolvedTypes = fieldTypes ?? Enumerable.Repeat(ScalarFieldType.String, fieldNames.Count).ToArray();
        if (resolvedTypes.Count != fieldNames.Count)
        {
            throw new ArgumentException(
                $"The field type list must match the field count for collection '{collectionName}'.",
                nameof(fieldTypes));
        }

        var fields = MapFieldDefinitions(fieldNames, resolvedTypes);
        Fields = AssignTypedColumnIndexes(fields);
        PrimaryKey = Fields[0];
        _fieldNameToIndex = Fields.ToFrozenDictionary(f => f.Name, f => f.FieldIndex);
    }

    private static FieldDefinition[] MapFieldDefinitions(IList<string> fieldNames, IList<ScalarFieldType> fieldTypes)
    {
        var fields = new FieldDefinition[fieldNames.Count + 1];
        fields[0] = new FieldDefinition("key", 0, ScalarFieldType.String);
        for (int i = 1; i <= fieldNames.Count; i++)
        {
            fields[i] = new FieldDefinition(fieldNames[i - 1], i, fieldTypes[i - 1]);
        }

        return fields;
    }

    private static IReadOnlyList<FieldDefinition> AssignTypedColumnIndexes(FieldDefinition[] fields)
    {
        var updated = new FieldDefinition[fields.Length];
        var int32Index = 0;
        var int64Index = 0;
        var doubleIndex = 0;
        var decimalIndex = 0;
        var dateOnlyIndex = 0;
        var dateTimeIndex = 0;
        var dateTimeOffsetIndex = 0;

        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            var typedIndex = field.Type switch
            {
                ScalarFieldType.Int32 => int32Index++,
                ScalarFieldType.Int64 => int64Index++,
                ScalarFieldType.Double => doubleIndex++,
                ScalarFieldType.Decimal => decimalIndex++,
                ScalarFieldType.DateOnly => dateOnlyIndex++,
                ScalarFieldType.DateTime => dateTimeIndex++,
                ScalarFieldType.DateTimeOffset => dateTimeOffsetIndex++,
                _ => -1
            };

            updated[i] = field with { TypedColumnIndex = typedIndex };
        }

        return updated;
    }

    public FieldDefinition GetFieldDefinition(string name)
    {
        return Fields[_fieldNameToIndex.GetValueOrDefault(name, -1)];
    }

    public FieldDefinition GetFieldDefinition(int fieldIndex)
    {
        if (fieldIndex < 0 || fieldIndex >= Fields.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldIndex));
        }

        return Fields[fieldIndex];
    }

    public bool TryGetFieldDefinition(string name, out FieldDefinition fieldDefinition)
    {
        if (_fieldNameToIndex.TryGetValue(name, out var fieldIndex))
        {
            fieldDefinition = Fields[fieldIndex];
            return true;
        }

        fieldDefinition = default!;
        return false;
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