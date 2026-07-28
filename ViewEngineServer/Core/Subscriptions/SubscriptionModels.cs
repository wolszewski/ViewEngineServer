using ViewEngineServer.Core.Views;

namespace ViewEngineServer.Core.Subscriptions;

// ---------------------------------------------------------------------------
// Viewport state — one per connected client
// ---------------------------------------------------------------------------

/// <summary>
/// Tracks the current viewport position for a single client connection.
/// Mutable; updated whenever the client changes page or the delta engine
/// confirms a new viewport snapshot.
/// </summary>
public sealed class ViewportState
{
    public required string ConnectionId { get; init; }
    public required ViewKey ViewKey { get; set; }
    public int StartIndex { get; set; }
    public int PageSize { get; set; }

    /// <summary>
    /// Ordered row ids currently rendered in this client's viewport.
    /// Used by the delta engine to diff before/after a mutation.
    /// </summary>
    public string[] CurrentRowIds { get; set; } = [];
}

// ---------------------------------------------------------------------------
// Subscription commands — transport-neutral; produced by any adapter
// ---------------------------------------------------------------------------

public abstract class SubscriptionCommand
{
    public required string ConnectionId { get; init; }
}

/// <summary>
/// Client requests a live view. The engine will respond with a full snapshot
/// and then push incremental delta events as data changes.
/// </summary>
public sealed class SubscribeCommand : SubscriptionCommand
{
    public required ViewDefinition View { get; init; }
    public int StartIndex { get; init; }
    public int PageSize { get; init; } = 50;
}

/// <summary>
/// Client scrolls or changes page within the existing view.
/// The engine responds with a fresh snapshot for the new range.
/// </summary>
public sealed class ChangeViewportCommand : SubscriptionCommand
{
    public int StartIndex { get; init; }
    public int PageSize { get; init; }
}

/// <summary>Client disconnects or explicitly unsubscribes.</summary>
public sealed class UnsubscribeCommand : SubscriptionCommand { }
