using System.Text.Json;
using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;

namespace ViewEngineServer.WebApp.WebSocket;

public sealed class JsonOutboundProtocolEncoder : IOutboundProtocolEncoder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public OutboundMessageFormat Format => OutboundMessageFormat.Json;

    public byte[] EncodeSubscriptionAccepted(SubscriptionAcceptedPayload payload)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new JsonSubscriptionAcceptedMessage
        {
            SubscriptionId = payload.SubscriptionId,
            SnapshotFollows = payload.SnapshotFollows,
            StartIndex = payload.StartIndex,
            TotalCount = payload.TotalCount,
            Fields = payload.Fields
        }, JsonOptions);
    }

    public IEnumerable<byte[]> EncodeFrames(ViewDelta delta, int subscriptionId)
    {
        switch (delta)
        {
            case SnapshotStartDelta start:
                yield return Serialize(new JsonSnapshotStartMessage
                {
                    SubscriptionId = subscriptionId,
                    StartIndex = start.StartIndex,
                    TotalCount = start.TotalCount,
                    IsPartial = start.IsPartial,
                    Fields = GetVisibleFieldNames(start.Schema, start.VisibleFieldIndexes)
                });
                yield break;
            case SnapshotRowsDelta rows:
                foreach (var row in rows.Rows)
                {
                    yield return Serialize(new JsonSnapshotRowMessage
                    {
                        SubscriptionId = subscriptionId,
                        Row = FormatRow(rows.Schema, row, rows.VisibleFieldIndexes)
                    });
                }
                yield break;
            case EndOfSnapshotDelta:
                yield return Serialize(new JsonEndOfSnapshotMessage { SubscriptionId = subscriptionId });
                yield break;
            case SnapshotDelta snapshot:
                yield return Serialize(new JsonSnapshotStartMessage
                {
                    SubscriptionId = subscriptionId,
                    StartIndex = snapshot.StartIndex,
                    TotalCount = snapshot.TotalCount,
                    IsPartial = snapshot.IsPartial,
                    Fields = GetVisibleFieldNames(snapshot.Schema, snapshot.VisibleFieldIndexes)
                });
                foreach (var row in snapshot.Rows)
                {
                    yield return Serialize(new JsonSnapshotRowMessage
                    {
                        SubscriptionId = subscriptionId,
                        Row = FormatRow(snapshot.Schema, row, snapshot.VisibleFieldIndexes)
                    });
                }
                yield return Serialize(new JsonEndOfSnapshotMessage { SubscriptionId = subscriptionId });
                yield break;
            case RowInsertDelta insert:
                yield return Serialize(new JsonRowInsertMessage
                {
                    SubscriptionId = subscriptionId,
                    Position = insert.Position,
                    Row = FormatRow(insert.Schema, insert.Row, insert.VisibleFieldIndexes)
                });
                yield break;
            case RowUpdateDelta update:
                yield return Serialize(new JsonRowUpdateMessage
                {
                    SubscriptionId = subscriptionId,
                    RowId = update.RowId,
                    Position = update.Position,
                    ChangedFields = FormatChanges(update.Schema, update.ChangedColumns)
                });
                yield break;
            case RowRemoveDelta remove:
                yield return Serialize(new JsonRowRemoveMessage
                {
                    SubscriptionId = subscriptionId,
                    RowId = remove.RowId,
                    Position = remove.Position
                });
                yield break;
            case RowReplaceDelta replace:
                yield return Serialize(new JsonRowReplaceMessage
                {
                    SubscriptionId = subscriptionId,
                    RemovedRowId = replace.RemovedRowId,
                    RemovePosition = replace.RemovePosition,
                    InsertPosition = replace.InsertPosition,
                    Row = FormatRow(replace.Schema, replace.Row, replace.VisibleFieldIndexes)
                });
                yield break;
            default:
                throw new ArgumentOutOfRangeException(nameof(delta), delta.GetType().Name, "Unknown delta type.");
        }
    }

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    private static IReadOnlyDictionary<string, string?> FormatRow(
        CollectionSchema schema,
        string?[] row,
        IReadOnlyList<int>? visibleFieldIndexes)
    {
        var fieldIndexes = visibleFieldIndexes ?? Enumerable.Range(0, schema.Fields.Count).ToArray();
        var result = new Dictionary<string, string?>(fieldIndexes.Count);
        for (int i = 0; i < fieldIndexes.Count; i++)
        {
            result[schema.Fields[fieldIndexes[i]].Name] = row[i];
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string?> FormatChanges(
        CollectionSchema schema,
        IReadOnlyCollection<KeyValuePair<int, string?>> changedColumns)
    {
        var result = new Dictionary<string, string?>(changedColumns.Count);
        foreach (var (fieldIndex, value) in changedColumns)
        {
            result[schema.Fields[fieldIndex].Name] = value;
        }

        return result;
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

    private abstract class JsonMessage
    {
        public string Type { get; init; } = string.Empty;
        public required int SubscriptionId { get; init; }
    }

    private sealed class JsonSubscriptionAcceptedMessage : JsonMessage
    {
        public JsonSubscriptionAcceptedMessage() => Type = "subscriptionAccepted";
        public required bool SnapshotFollows { get; init; }
        public required int StartIndex { get; init; }
        public required int TotalCount { get; init; }
        public required IReadOnlyList<string> Fields { get; init; }
    }

    private sealed class JsonSnapshotStartMessage : JsonMessage
    {
        public JsonSnapshotStartMessage() => Type = "snapshotStart";
        public required int StartIndex { get; init; }
        public required int TotalCount { get; init; }
        public bool IsPartial { get; init; }
        public IReadOnlyList<string> Fields { get; init; } = [];
    }

    private sealed class JsonSnapshotRowMessage : JsonMessage
    {
        public JsonSnapshotRowMessage() => Type = "snapshotRow";
        public required IReadOnlyDictionary<string, string?> Row { get; init; }
    }

    private sealed class JsonEndOfSnapshotMessage : JsonMessage
    {
        public JsonEndOfSnapshotMessage() => Type = "eos";
    }

    private sealed class JsonRowInsertMessage : JsonMessage
    {
        public JsonRowInsertMessage() => Type = "rowInsert";
        public required int Position { get; init; }
        public required IReadOnlyDictionary<string, string?> Row { get; init; }
    }

    private sealed class JsonRowUpdateMessage : JsonMessage
    {
        public JsonRowUpdateMessage() => Type = "rowUpdate";
        public required string RowId { get; init; }
        public required int Position { get; init; }
        public required IReadOnlyDictionary<string, string?> ChangedFields { get; init; }
    }

    private sealed class JsonRowRemoveMessage : JsonMessage
    {
        public JsonRowRemoveMessage() => Type = "rowRemove";
        public required string RowId { get; init; }
        public required int Position { get; init; }
    }

    private sealed class JsonRowReplaceMessage : JsonMessage
    {
        public JsonRowReplaceMessage() => Type = "rowReplace";
        public required string RemovedRowId { get; init; }
        public required int RemovePosition { get; init; }
        public required int InsertPosition { get; init; }
        public required IReadOnlyDictionary<string, string?> Row { get; init; }
    }
}
