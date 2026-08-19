using System.Text;
using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using System.Linq;

namespace ViewEngineServer.WebApp.WebSocket;

public sealed class CompactOutboundProtocolEncoder : IOutboundProtocolEncoder
{
    private const char Separator = '|';
    private const char Escape = '\\';
    private const char NullToken = '~';
    public OutboundMessageFormat Format => OutboundMessageFormat.Compact;

    public byte[] EncodeSubscriptionAccepted(SubscriptionAcceptedPayload payload)
    {
        var builder = new StringBuilder();
        builder.Append("A|").Append(payload.SubscriptionId).Append(Separator)
            .Append(payload.SnapshotFollows ? '1' : '0').Append(Separator)
            .Append(payload.StartIndex).Append(Separator)
            .Append(payload.TotalCount);
        AppendFieldNames(builder, payload.Fields);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public IEnumerable<byte[]> EncodeFrames(ViewDelta delta, int subscriptionId)
    {
        switch (delta)
        {
            case SnapshotStartDelta start:
                yield return EncodeSnapshotStart(subscriptionId, start.StartIndex, start.TotalCount, start.IsPartial);
                yield break;
            case SnapshotRowsDelta rows:
                foreach (var row in rows.Rows)
                {
                    yield return EncodeSnapshotRow(subscriptionId, rows.Schema, rows.VisibleFieldIndexes, row);
                }
                 yield break;
            case EndOfSnapshotDelta:
                yield return Encoding.UTF8.GetBytes($"EOS|{subscriptionId}");
                yield break;
            case SnapshotDelta snapshot:
                yield return EncodeSnapshotStart(subscriptionId, snapshot.StartIndex, snapshot.TotalCount, snapshot.IsPartial);
                foreach (var row in snapshot.Rows)
                {
                    yield return EncodeSnapshotRow(subscriptionId, snapshot.Schema, snapshot.VisibleFieldIndexes, row);
                }
                yield return Encoding.UTF8.GetBytes($"EOS|{subscriptionId}");
                yield break;
            case RowInsertDelta insert:
                yield return EncodeInsert(subscriptionId, insert.Schema, insert.VisibleFieldIndexes, insert.Position, insert.Row);
                yield break;
            case RowUpdateDelta update:
                yield return EncodeUpdate(subscriptionId, update.Schema, update.VisibleFieldIndexes, update.RowId, update.Position, update.ChangedColumns);
                yield break;
            case RowRemoveDelta remove:
                yield return Encoding.UTF8.GetBytes($"D|{subscriptionId}|{EscapeValue(remove.RowId)}|{remove.Position}");
                yield break;
            case RowReplaceDelta replace:
                yield return EncodeReplace(
                    subscriptionId,
                    replace.Schema,
                    replace.VisibleFieldIndexes,
                    replace.RemovedRowId,
                    replace.RemovePosition,
                    replace.InsertPosition,
                    replace.Row);
                yield break;
            default:
                throw new ArgumentOutOfRangeException(nameof(delta), delta.GetType().Name, "Unknown delta type.");
        }
    }

    private static byte[] EncodeSnapshotStart(int subscriptionId, int startIndex, int totalCount, bool isPartial = false)
    {
        var frame = isPartial
            ? $"P|{subscriptionId}|{startIndex}|{totalCount}|1"
            : $"P|{subscriptionId}|{startIndex}|{totalCount}";
        return Encoding.UTF8.GetBytes(frame);
    }

    private byte[] EncodeSnapshotRow(
        int subscriptionId,
        CollectionSchema schema,
        IReadOnlyList<int>? visibleFieldIndexes,
        string?[] row)
    {
        var builder = new StringBuilder();
        builder.Append("S|").Append(subscriptionId).Append(Separator);
        AppendKey(builder, schema, visibleFieldIndexes, row);
        AppendFullRow(builder, schema, visibleFieldIndexes, row);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private byte[] EncodeInsert(
        int subscriptionId,
        CollectionSchema schema,
        IReadOnlyList<int>? visibleFieldIndexes,
        int position,
        string?[] row)
    {
        var builder = new StringBuilder();
        builder.Append("I|").Append(subscriptionId).Append(Separator);
        AppendKey(builder, schema, visibleFieldIndexes, row);
        builder.Append(Separator).Append(position);
        AppendFullRow(builder, schema, visibleFieldIndexes, row);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private byte[] EncodeUpdate(
        int subscriptionId,
        CollectionSchema schema,
        IReadOnlyList<int>? visibleFieldIndexes,
        string rowId,
        int position,
        IReadOnlyCollection<KeyValuePair<int, string?>> changedColumns)
    {
        var orderedFieldIndexes = OutboundProtocolEncodingHelpers.GetPayloadFieldIndexes(schema, visibleFieldIndexes);
        var changedByField = changedColumns.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value);

        var builder = new StringBuilder();
        builder.Append("U|").Append(subscriptionId).Append(Separator)
            .Append(EscapeValue(rowId)).Append(Separator)
            .Append(position);

        int i = 0;
        while (i < orderedFieldIndexes.Count)
        {
            int fieldIndex = orderedFieldIndexes[i];
            if (changedByField.TryGetValue(fieldIndex, out string? value))
            {
                builder.Append(Separator);
                AppendValue(builder, value);
                i++;
                continue;
            }

            int skipCount = 1;
            while (i + skipCount < orderedFieldIndexes.Count &&
                   !changedByField.ContainsKey(orderedFieldIndexes[i + skipCount]))
            {
                skipCount++;
            }

            builder.Append(Separator).Append('^').Append(skipCount);
            i += skipCount;
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private byte[] EncodeReplace(
        int subscriptionId,
        CollectionSchema schema,
        IReadOnlyList<int>? visibleFieldIndexes,
        string removedRowId,
        int removePosition,
        int insertPosition,
        string?[] row)
    {
        var builder = new StringBuilder();
        builder.Append("R|").Append(subscriptionId).Append(Separator)
            .Append(EscapeValue(removedRowId)).Append(Separator)
            .Append(removePosition).Append(Separator)
            .Append(insertPosition).Append(Separator);
        AppendKey(builder, schema, visibleFieldIndexes, row);
        AppendFullRow(builder, schema, visibleFieldIndexes, row);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void AppendFieldNames(StringBuilder builder, IReadOnlyList<string> fields)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            builder.Append(Separator).Append(EscapeValue(fields[i]));
        }
    }

    private static void AppendKey(
        StringBuilder builder,
        CollectionSchema schema,
        IReadOnlyList<int>? visibleFieldIndexes,
        string?[] row)
    {
        int keyIndex = OutboundProtocolEncodingHelpers.FindSelectedFieldPosition(schema.PrimaryKey.FieldIndex, visibleFieldIndexes);
        AppendValue(builder, row[keyIndex]);
    }

    private static void AppendFullRow(
        StringBuilder builder,
        CollectionSchema schema,
        IReadOnlyList<int>? visibleFieldIndexes,
        string?[] row)
    {
        var fieldIndexes = OutboundProtocolEncodingHelpers.GetPayloadFieldIndexes(schema, visibleFieldIndexes);
        foreach (int fieldIndex in fieldIndexes)
        {
            builder.Append(Separator);
            int rowIndex = OutboundProtocolEncodingHelpers.FindSelectedFieldPosition(fieldIndex, visibleFieldIndexes);
            AppendValue(builder, row[rowIndex]);
        }
    }

    private static void AppendValue(StringBuilder builder, string? value) => builder.Append(EscapeValue(value));

    private static string EscapeValue(string? value)
    {
        if (value is null)
        {
            return NullToken.ToString();
        }

        if (value.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            if (ch is Separator or Escape or '^' or NullToken)
            {
                builder.Append(Escape);
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
