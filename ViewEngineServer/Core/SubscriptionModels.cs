namespace ViewEngineServer.Core;


public sealed class ViewportState
{
    public required string ConnectionId { get; init; }
    public required ViewKey ViewKey { get; set; }
    public int StartIndex { get; set; }
    public int PageSize { get; set; }

    public string[] CurrentRowIds { get; set; } = [];
}


public abstract class SubscriptionCommand
{
    public required string ConnectionId { get; init; }
}

public sealed class SubscribeCommand : SubscriptionCommand
{
    public required ViewDefinition View { get; init; }
    public int StartIndex { get; init; }
    public int PageSize { get; init; } = 50;
}

public sealed class ChangeViewportCommand : SubscriptionCommand
{
    public int StartIndex { get; init; }
    public int PageSize { get; init; }
}

public sealed class UnsubscribeCommand : SubscriptionCommand { }
