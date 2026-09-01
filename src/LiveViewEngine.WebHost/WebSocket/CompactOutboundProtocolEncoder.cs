using System.Buffers;
using System.Buffers.Text;
using System.Text;
using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using System.Linq;

namespace ViewEngineServer.WebApp.WebSocket;

public sealed class CompactOutboundProtocolEncoder : IOutboundProtocolEncoder
{
    private const byte SeparatorByte = (byte)'|';
    private const byte EscapeByte = (byte)'\\';
    private const byte SkipMarkerByte = (byte)'^';
    private const byte NullTokenByte = (byte)'~';

    private static readonly byte[] SeparatorSpan = [(byte)'|'];
    private static readonly byte[] EscapeSpan = [(byte)'\\'];
    private static readonly byte[] ASpan = [(byte)'A'];
    private static readonly byte[] SSpan = [(byte)'S'];
    private static readonly byte[] ISpan = [(byte)'I'];
    private static readonly byte[] USpan = [(byte)'U'];
    private static readonly byte[] DSpan = [(byte)'D'];
    private static readonly byte[] RSpan = [(byte)'R'];
    private static readonly byte[] PSpan = [(byte)'P'];
    private static readonly byte[] SkipSpan = [(byte)'^'];
    private static readonly byte[] OneByte = [(byte)'1'];

    public OutboundMessageFormat Format => OutboundMessageFormat.Compact;

    public byte[] EncodeSubscriptionAccepted(SubscriptionAcceptedPayload payload)
    {
        var writer = new ArrayBufferWriter<byte>();
        writer.Write(ASpan);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, payload.SubscriptionId);
        writer.Write(SeparatorSpan);
        writer.Write(payload.SnapshotFollows ? OneByte : SeparatorSpan);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, payload.StartIndex);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, payload.TotalCount);

        for (int i = 0; i < payload.Fields.Count; i++)
        {
            writer.Write(SeparatorSpan);
            WriteEscaped(writer, payload.Fields[i]);
        }

        return writer.WrittenMemory.ToArray();
    }

    public IEnumerable<byte[]> EncodeFrames(ViewDelta delta, int subscriptionId)
    {
        switch (delta)
        {
            case SnapshotStartDelta start:
                yield return EncodeSnapshotStart(
                    subscriptionId,
                    start.StartIndex,
                    start.TotalCount,
                    start.IsPartial,
                    start.Schema,
                    start.VisibleFieldIndexes);
                yield break;
            case SnapshotRowsDelta rows:
                for (int i = 0; i < rows.Rows.Count; i++)
                {
                    yield return EncodeSnapshotRow(
                        subscriptionId,
                        rows.Schema,
                        rows.VisibleFieldIndexes,
                        rows.RowNumbers[i],
                        rows.Rows[i]);
                }
                yield break;
            case EndOfSnapshotDelta:
                yield return EncodeEndOfSnapshot(subscriptionId);
                yield break;
            case SnapshotDelta snapshot:
                yield return EncodeSnapshotStart(
                    subscriptionId,
                    snapshot.StartIndex,
                    snapshot.TotalCount,
                    snapshot.IsPartial,
                    snapshot.Schema,
                    snapshot.VisibleFieldIndexes);
                for (int i = 0; i < snapshot.Rows.Count; i++)
                {
                    yield return EncodeSnapshotRow(
                        subscriptionId,
                        snapshot.Schema,
                        snapshot.VisibleFieldIndexes,
                        snapshot.StartIndex + i,
                        snapshot.Rows[i]);
                }
                yield return EncodeEndOfSnapshot(subscriptionId);
                yield break;
            case RowInsertDelta insert:
                yield return EncodeInsert(subscriptionId, insert.Schema, insert.VisibleFieldIndexes, insert.Position, insert.Row);
                yield break;
            case RowUpdateDelta update:
                yield return EncodeUpdate(subscriptionId, update.Schema, update.VisibleFieldIndexes, update.RowId, update.Position, update.ChangedColumns);
                yield break;
            case RowRemoveDelta remove:
                yield return EncodeDelete(subscriptionId, remove.RowId, remove.Position);
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

    private static byte[] EncodeSnapshotStart(
        int subscriptionId,
        int startIndex,
        int totalCount,
        bool isPartial = false,
        CollectionSchema? schema = null,
        IReadOnlyList<int>? visibleFieldIndexes = null)
    {
        var writer = new ArrayBufferWriter<byte>();
        writer.Write(PSpan);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, subscriptionId);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, startIndex);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, totalCount);
        if (isPartial)
        {
            writer.Write(SeparatorSpan);
            writer.Write(OneByte);
        }

        if (schema is not null)
        {
            foreach (var fieldName in GetVisibleFieldNames(schema, visibleFieldIndexes))
            {
                writer.Write(SeparatorSpan);
                WriteEscaped(writer, fieldName);
            }
        }

        return writer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeEndOfSnapshot(int subscriptionId)
    {
        var writer = new ArrayBufferWriter<byte>();
        WriteBytes(writer, "EOS"u8);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, subscriptionId);
        return writer.WrittenMemory.ToArray();
    }

    private byte[] EncodeSnapshotRow(
        int subscriptionId,
        CollectionSchema schema,
        IReadOnlyList<int>? visibleFieldIndexes,
        int rowNumber,
        string?[] row)
    {
        var writer = new ArrayBufferWriter<byte>();
        writer.Write(SSpan);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, subscriptionId);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, rowNumber);
        writer.Write(SeparatorSpan);
        WriteKeyField(writer, schema, visibleFieldIndexes, row);
        WriteFullRow(writer, schema, visibleFieldIndexes, row);
        return writer.WrittenMemory.ToArray();
    }

    private byte[] EncodeInsert(
        int subscriptionId,
        CollectionSchema schema,
        IReadOnlyList<int>? visibleFieldIndexes,
        int position,
        string?[] row)
    {
        var writer = new ArrayBufferWriter<byte>();
        writer.Write(ISpan);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, subscriptionId);
        writer.Write(SeparatorSpan);
        WriteKeyField(writer, schema, visibleFieldIndexes, row);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, position);
        WriteFullRow(writer, schema, visibleFieldIndexes, row);
        return writer.WrittenMemory.ToArray();
    }

    private byte[] EncodeUpdate(
        int subscriptionId,
        CollectionSchema schema,
        IReadOnlyList<int>? visibleFieldIndexes,
        string rowId,
        int position,
        IReadOnlyCollection<KeyValuePair<int, string?>> changedColumns)
    {
        var writer = new ArrayBufferWriter<byte>();
        writer.Write(USpan);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, subscriptionId);
        writer.Write(SeparatorSpan);
        WriteEscaped(writer, rowId);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, position);

        var orderedFieldIndexes = OutboundProtocolEncodingHelpers.GetPayloadFieldIndexes(schema, visibleFieldIndexes);
        var changedByField = changedColumns.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value);

        int i = 0;
        while (i < orderedFieldIndexes.Count)
        {
            int fieldIndex = orderedFieldIndexes[i];
            if (changedByField.TryGetValue(fieldIndex, out string? value))
            {
                writer.Write(SeparatorSpan);
                WriteEscaped(writer, value);
                i++;
                continue;
            }

            int skipCount = 1;
            while (i + skipCount < orderedFieldIndexes.Count &&
                   !changedByField.ContainsKey(orderedFieldIndexes[i + skipCount]))
            {
                skipCount++;
            }

            writer.Write(SeparatorSpan);
            writer.Write(SkipSpan);
            WriteInt32(writer, skipCount);
            i += skipCount;
        }

        return writer.WrittenMemory.ToArray();
    }

    private byte[] EncodeDelete(int subscriptionId, string rowId, int position)
    {
        var writer = new ArrayBufferWriter<byte>();
        writer.Write(DSpan);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, subscriptionId);
        writer.Write(SeparatorSpan);
        WriteEscaped(writer, rowId);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, position);
        return writer.WrittenMemory.ToArray();
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
        var writer = new ArrayBufferWriter<byte>();
        writer.Write(RSpan);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, subscriptionId);
        writer.Write(SeparatorSpan);
        WriteEscaped(writer, removedRowId);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, removePosition);
        writer.Write(SeparatorSpan);
        WriteInt32(writer, insertPosition);
        writer.Write(SeparatorSpan);
        WriteKeyField(writer, schema, visibleFieldIndexes, row);
        WriteFullRow(writer, schema, visibleFieldIndexes, row);
        return writer.WrittenMemory.ToArray();
    }

    private static IReadOnlyList<string> GetVisibleFieldNames(
        CollectionSchema schema,
        IReadOnlyList<int>? visibleFieldIndexes)
    {
        var indexes = visibleFieldIndexes ?? Enumerable.Range(0, schema.Fields.Count).ToArray();
        var names = new List<string>(indexes.Count);
        foreach (var index in indexes)
        {
            if (index == CollectionSchema.PrimaryKeyIndex)
            {
                continue;
            }

            names.Add(schema.Fields[index].Name);
        }

        return names;
    }

    private static void WriteKeyField(
        ArrayBufferWriter<byte> writer,
        CollectionSchema schema,
        IReadOnlyList<int>? visibleFieldIndexes,
        string?[] row)
    {
        int keyIndex = OutboundProtocolEncodingHelpers.FindSelectedFieldPosition(schema.PrimaryKey.FieldIndex, visibleFieldIndexes);
        WriteEscaped(writer, row[keyIndex]);
    }

    private static void WriteFullRow(
        ArrayBufferWriter<byte> writer,
        CollectionSchema schema,
        IReadOnlyList<int>? visibleFieldIndexes,
        string?[] row)
    {
        var fieldIndexes = OutboundProtocolEncodingHelpers.GetPayloadFieldIndexes(schema, visibleFieldIndexes);
        foreach (int fieldIndex in fieldIndexes)
        {
            writer.Write(SeparatorSpan);
            int rowIndex = OutboundProtocolEncodingHelpers.FindSelectedFieldPosition(fieldIndex, visibleFieldIndexes);
            WriteEscaped(writer, row[rowIndex]);
        }
    }

    private static void WriteEscaped(ArrayBufferWriter<byte> writer, string? value)
    {
        if (value is null)
        {
            writer.Write([NullTokenByte]);
            return;
        }

        if (value.Length == 0)
        {
            return;
        }

        var buffer = writer.GetSpan(value.Length * 4);
        int bytesWritten = 0;

        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            if (ch is '|' or '\\' or '^' or '~')
            {
                buffer[bytesWritten++] = EscapeByte;
            }

            int charBytes = Encoding.UTF8.GetBytes(value.AsSpan(i, 1), buffer[bytesWritten..]);
            bytesWritten += charBytes;
        }

        writer.Advance(bytesWritten);
    }

    private static void WriteBytes(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> bytes)
    {
        writer.Write(bytes);
    }

    private static void WriteInt32(ArrayBufferWriter<byte> writer, int value)
    {
        Span<byte> buffer = writer.GetSpan(16);
        bool success = Utf8Formatter.TryFormat(value, buffer, out int bytesWritten);
        System.Diagnostics.Debug.Assert(success);
        writer.Advance(bytesWritten);
    }
}
