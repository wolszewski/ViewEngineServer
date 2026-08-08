using LiveViewEngine.Core.Views;

namespace LiveViewEngine.Core;


public sealed class ViewportState
{
    public required string ConnectionId { get; init; }
    public required ViewKey ViewKey { get; set; }
    public int StartIndex { get; set; }
    public int? PageSize { get; set; }

    public int[] CurrentRowIndexes { get; set; } = [];
}


public abstract class SubscriptionCommand
{
    public required string ConnectionId { get; init; }
}

public sealed class SubscribeCommand : SubscriptionCommand
{
    public required ViewDefinition View { get; init; }
    public int StartIndex { get; init; } = 0;
    public int? PageSize { get; init; }
}

public sealed class ChangeViewportCommand : SubscriptionCommand
{
    public int StartIndex { get; init; } = 0;
    public int? PageSize { get; init; } = null;
}

public sealed class UnsubscribeCommand : SubscriptionCommand { }
