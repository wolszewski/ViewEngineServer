using LiveViewEngine.Core.Data;

namespace ViewEngineServer.WebApp.WebSocket;

internal static class OutboundProtocolEncodingHelpers
{
    public static int FindSelectedFieldPosition(int fieldIndex, IReadOnlyList<int>? visibleFieldIndexes)
    {
        if (visibleFieldIndexes is null)
        {
            return fieldIndex;
        }

        for (int i = 0; i < visibleFieldIndexes.Count; i++)
        {
            if (visibleFieldIndexes[i] == fieldIndex)
            {
                return i;
            }
        }

        throw new ArgumentException($"Field index '{fieldIndex}' was not selected.");
    }

    public static List<int> GetPayloadFieldIndexes(CollectionSchema schema, IReadOnlyList<int>? visibleFieldIndexes)
    {
        var fieldIndexes = visibleFieldIndexes?.Where(i => i != schema.PrimaryKey.FieldIndex).ToList();
        if (fieldIndexes is { Count: > 0 })
        {
            return fieldIndexes;
        }

        return Enumerable.Range(0, schema.Fields.Count)
            .Where(i => i != schema.PrimaryKey.FieldIndex)
            .ToList();
    }
}
