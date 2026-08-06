using System.Collections.Concurrent;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.Output;

public interface IRowOutputFormatter
{
    IReadOnlyDictionary<string, string?> FormatRow(RowCollection collection, int rowIndex);
}

public sealed class JsonDictionaryRowOutputFormatter : IRowOutputFormatter
{
    private readonly ConcurrentDictionary<CollectionSchema, string[]> _fieldNamesBySchema = new();

    public IReadOnlyDictionary<string, string?> FormatRow(RowCollection collection, int rowIndex)
    {
        var fieldNames = _fieldNamesBySchema.GetOrAdd(collection.Schema, static schema =>
        {
            var names = new string[schema.Fields.Count];
            for (int i = 0; i < schema.Fields.Count; i++)
            {
                names[i] = schema.Fields[i].Name;
            }

            return names;
        });

        var values = collection.GetRowValues(rowIndex);
        var row = new Dictionary<string, string?>(fieldNames.Length);
        for (int i = 0; i < fieldNames.Length; i++)
        {
            row[fieldNames[i]] = values[i];
        }

        return row;
    }
}
