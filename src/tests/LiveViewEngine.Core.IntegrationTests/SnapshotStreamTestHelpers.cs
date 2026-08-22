using LiveViewEngine.Core;

namespace LiveViewEngine.Core.IntegrationTests;

internal static class SnapshotStreamTestHelpers
{
    public static SnapshotDelta ToSnapshotDelta(this IReadOnlyList<ViewDelta> deltas)
    {
        if (deltas.Count == 1 && deltas[0] is SnapshotDelta snapshot)
        {
            return snapshot;
        }

        var start = deltas.OfType<SnapshotStartDelta>().First();
        var rows = deltas.OfType<SnapshotRowsDelta>().SelectMany(static delta => delta.Rows).ToArray();

        return new SnapshotDelta
        {
            ViewId = start.ViewId,
            Schema = start.Schema,
            TotalCount = start.TotalCount,
            StartIndex = start.StartIndex,
            Rows = rows,
            IsPartial = start.IsPartial,
            VisibleFieldIndexes = start.VisibleFieldIndexes
        };
    }
}
