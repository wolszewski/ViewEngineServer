using LiveViewEngine.Core.Views;

namespace LiveViewEngine.Core;


public readonly record struct SubscriptionKey(int ConnectionId, int SubscriptionId)
{
    public override string ToString() => $"{ConnectionId}:{SubscriptionId}";
}

public readonly record struct SubscriberTarget(int ConnectionId, int SubscriptionId);

public sealed class ViewportState
{
    public required SubscriptionKey SubscriptionKey { get; init; }
    public required ViewDefinition View { get; set; }
    public required ViewKey ViewKey { get; set; }
    public int StartIndex { get; set; }
    public int? PageSize { get; set; }
    public FieldMask VisibleColumns { get; set; }
    public int[] SelectedFieldIndexes { get; set; } = [];
}


public abstract class SubscriptionCommand
{
    public int ConnectionId { get; init; }
    public int SubscriptionId { get; init; }

    public SubscriptionKey EffectiveSubscriptionKey => new(ConnectionId, SubscriptionId);
}

public sealed class SubscribeCommand : SubscriptionCommand
{
    public required ViewDefinition View { get; init; }
    public int StartIndex { get; init; }
    public int? PageSize { get; init; }
    public bool SendSnapshot { get; init; } = true;
    public bool ResumeAfterAccepted { get; init; }
}

public enum SubscribeSnapshotFollows
{
    None,
    Immediate,
    Pending
}

public enum SnapshotMode
{
    No,
    Delta,
    Full
}

public class UpdateViewCommand : SubscriptionCommand
{
    public int? StartIndex { get; init; }
    public int? PageSize { get; init; }
    public string? SortColumn { get; init; }
    public bool? SortAscending { get; init; }
    public IReadOnlyList<FilterSpec>? Filters { get; init; }
    public IReadOnlyList<string>? Fields { get; init; }
    public SnapshotMode SnapshotMode { get; init; } = SnapshotMode.Delta;
}

public sealed class ChangeViewportCommand : UpdateViewCommand { }

public sealed class UnsubscribeCommand : SubscriptionCommand { }
