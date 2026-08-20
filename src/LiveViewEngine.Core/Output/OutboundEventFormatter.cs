namespace LiveViewEngine.Core.Output;

public interface IOutboundEventFormatter
{
    IReadOnlyList<DeltaEvent> Format(IReadOnlyList<ViewDelta> deltas, int subscriptionId = 0);
}

public sealed class JsonOutboundEventFormatter : IOutboundEventFormatter
{
    public IReadOnlyList<DeltaEvent> Format(IReadOnlyList<ViewDelta> deltas, int subscriptionId = 0)
    {
        var events = new DeltaEvent[deltas.Count];
        for (int i = 0; i < deltas.Count; i++)
        {
            events[i] = Format(deltas[i], subscriptionId);
        }

        return events;
    }

    private static DeltaEvent Format(ViewDelta delta, int subscriptionId)
    {
        return delta switch
        {
            SnapshotDelta snapshot => new SnapshotEvent
            {
                ViewId = snapshot.ViewId,
                SubscriptionId = subscriptionId,
                TotalCount = snapshot.TotalCount,
                StartIndex = snapshot.StartIndex,
                Rows = FormatRows(snapshot.Schema.Fields, snapshot.Rows, snapshot.VisibleFieldIndexes)
            },
            RowInsertDelta insert => new RowInsertEvent
            {
                ViewId = insert.ViewId,
                SubscriptionId = subscriptionId,
                Position = insert.Position,
                Row = FormatRow(insert.Schema.Fields, insert.Row, insert.VisibleFieldIndexes)
            },
            RowUpdateDelta update => new RowUpdateEvent
            {
                ViewId = update.ViewId,
                SubscriptionId = subscriptionId,
                RowId = update.RowId,
                Position = update.Position,
                ChangedFields = FormatChanges(update.Schema.Fields, update.ChangedColumns)
            },
            RowRemoveDelta remove => new RowRemoveEvent
            {
                ViewId = remove.ViewId,
                SubscriptionId = subscriptionId,
                Position = remove.Position
            },
            RowReplaceDelta replace => new RowReplaceEvent
            {
                ViewId = replace.ViewId,
                SubscriptionId = subscriptionId,
                RemovedRowId = replace.RemovedRowId,
                RemovePosition = replace.RemovePosition,
                InsertPosition = replace.InsertPosition,
                Row = FormatRow(replace.Schema.Fields, replace.Row, replace.VisibleFieldIndexes)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(delta), delta.GetType().Name, "Unknown delta type.")
        };
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string?>> FormatRows(
        IReadOnlyList<Data.FieldDefinition> fields,
    IReadOnlyList<string?[]> rows,
    IReadOnlyList<int>? visibleFieldIndexes)
    {
    var result = new IReadOnlyDictionary<string, string?>[rows.Count];
    for (int i = 0; i < rows.Count; i++)
    {
        result[i] = FormatRow(fields, rows[i], visibleFieldIndexes);
    }

    return result;
    }

    private static IReadOnlyDictionary<string, string?> FormatRow(
        IReadOnlyList<Data.FieldDefinition> fields,
        string?[] values,
        IReadOnlyList<int>? visibleFieldIndexes)
    {
        if (visibleFieldIndexes is null)
        {
            var allFieldsRow = new Dictionary<string, string?>(fields.Count);
            for (int i = 0; i < fields.Count; i++)
            {
                allFieldsRow[fields[i].Name] = values[i];
            }

            return allFieldsRow;
        }

        var row = new Dictionary<string, string?>(visibleFieldIndexes.Count);
        for (int i = 0; i < visibleFieldIndexes.Count; i++)
        {
            row[fields[visibleFieldIndexes[i]].Name] = values[i];
        }

        return row;
    }

    private static IReadOnlyDictionary<string, string?> FormatChanges(
        IReadOnlyList<Data.FieldDefinition> fields,
        IReadOnlyCollection<KeyValuePair<int, string?>> changedColumns)
    {
        var changes = new Dictionary<string, string?>(changedColumns.Count);
        foreach (var (fieldIndex, value) in changedColumns)
        {
            changes[fields[fieldIndex].Name] = value;
        }

        return changes;
    }
}
