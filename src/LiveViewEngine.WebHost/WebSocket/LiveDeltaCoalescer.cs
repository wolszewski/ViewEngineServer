using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;

namespace ViewEngineServer.WebApp.WebSocket;

internal static class LiveDeltaCoalescer
{
    public static bool TryQueueCoalescedDelta(SubscriptionState subscription, ViewDelta delta)
    {
        switch (delta)
        {
            case RowUpdateDelta update:
                var updateIndex = FindPendingRowIndex(subscription.PendingLiveDeltas, update.RowId);
                if (updateIndex >= 0 && subscription.PendingLiveDeltas[updateIndex] is RowUpdateDelta existingRowUpdate)
                {
                    subscription.PendingLiveDeltas[updateIndex] = MergeRowUpdate(existingRowUpdate, update);
                    return true;
                }

                subscription.PendingLiveDeltas.Add(update);
                return true;
            case RowInsertDelta insert:
                var insertRowId = GetRowId(insert.Schema, insert.Row);
                var insertIndex = FindPendingRowIndex(subscription.PendingLiveDeltas, insertRowId);
                if (insertIndex >= 0)
                {
                    if (subscription.PendingLiveDeltas[insertIndex] is RowRemoveDelta)
                    {
                        subscription.PendingLiveDeltas.RemoveAt(insertIndex);
                        return true;
                    }

                    if (subscription.PendingLiveDeltas[insertIndex] is RowInsertDelta previousInsert)
                    {
                        subscription.PendingLiveDeltas[insertIndex] = MergeRowInsert(previousInsert, insert);
                        return true;
                    }

                    if (subscription.PendingLiveDeltas[insertIndex] is RowUpdateDelta previousUpdateForInsert)
                    {
                        subscription.PendingLiveDeltas[insertIndex] = MergeRowInsert(previousUpdateForInsert, insert);
                        return true;
                    }
                }

                subscription.PendingLiveDeltas.Add(insert);
                return true;
            case RowRemoveDelta remove:
                var removeIndex = FindPendingRowIndex(subscription.PendingLiveDeltas, remove.RowId);
                if (removeIndex >= 0)
                {
                    if (subscription.PendingLiveDeltas[removeIndex] is RowInsertDelta)
                    {
                        subscription.PendingLiveDeltas.RemoveAt(removeIndex);
                        return true;
                    }

                    if (subscription.PendingLiveDeltas[removeIndex] is RowUpdateDelta)
                    {
                        subscription.PendingLiveDeltas[removeIndex] = remove;
                        return true;
                    }

                    if (subscription.PendingLiveDeltas[removeIndex] is RowRemoveDelta)
                    {
                        return true;
                    }
                }

                subscription.PendingLiveDeltas.Add(remove);
                return true;
            default:
                return false;
        }
    }

    private static int FindPendingRowIndex(IReadOnlyList<ViewDelta> pendingLiveDeltas, string rowId)
    {
        for (var i = 0; i < pendingLiveDeltas.Count; i++)
        {
            if (pendingLiveDeltas[i] is RowUpdateDelta update && update.RowId == rowId)
            {
                return i;
            }

            if (pendingLiveDeltas[i] is RowInsertDelta insert && GetRowId(insert.Schema, insert.Row) == rowId)
            {
                return i;
            }

            if (pendingLiveDeltas[i] is RowRemoveDelta remove && remove.RowId == rowId)
            {
                return i;
            }
        }

        return -1;
    }

    private static RowUpdateDelta MergeRowUpdate(RowUpdateDelta existing, RowUpdateDelta incoming)
    {
        var updatedFields = new Dictionary<int, string?>(existing.ChangedColumns.Count + incoming.ChangedColumns.Count);
        foreach (var (fieldIndex, value) in existing.ChangedColumns)
        {
            updatedFields[fieldIndex] = value;
        }

        foreach (var (fieldIndex, value) in incoming.ChangedColumns)
        {
            updatedFields[fieldIndex] = value;
        }

        return new RowUpdateDelta
        {
            ViewId = existing.ViewId,
            Schema = existing.Schema,
            RowId = existing.RowId,
            Position = incoming.Position,
            VisibleFieldIndexes = existing.VisibleFieldIndexes ?? incoming.VisibleFieldIndexes,
            ChangedColumns = updatedFields.OrderBy(static kvp => kvp.Key).ToList()
        };
    }

    private static RowInsertDelta MergeRowInsert(RowInsertDelta existing, RowInsertDelta incoming)
    {
        var row = (string?[])existing.Row.Clone();
        for (var i = 0; i < incoming.Row.Length; i++)
        {
            row[i] = incoming.Row[i];
        }

        return new RowInsertDelta
        {
            ViewId = existing.ViewId,
            Schema = existing.Schema,
            Position = incoming.Position,
            VisibleFieldIndexes = existing.VisibleFieldIndexes ?? incoming.VisibleFieldIndexes,
            Row = row
        };
    }

    private static RowInsertDelta MergeRowInsert(RowUpdateDelta existing, RowInsertDelta incoming)
    {
        var row = (string?[])incoming.Row.Clone();
        foreach (var (fieldIndex, value) in existing.ChangedColumns)
        {
            row[fieldIndex] = value;
        }

        return new RowInsertDelta
        {
            ViewId = existing.ViewId,
            Schema = existing.Schema,
            Position = incoming.Position,
            VisibleFieldIndexes = existing.VisibleFieldIndexes ?? incoming.VisibleFieldIndexes,
            Row = row
        };
    }

    private static string GetRowId(CollectionSchema schema, string?[] row)
    {
        return row[schema.PrimaryKey.FieldIndex] ?? string.Empty;
    }
}
