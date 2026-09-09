namespace LiveViewEngine.WebHost.LoadTests.Protocol;

/// <summary>
/// Union-style DTO covering every outbound WS message shape documented in
/// docs/subscription-design.md. Only the fields relevant to a given <see cref="Type"/> are set.
/// </summary>
public sealed class WsServerMessage
{
    public string Type { get; set; } = string.Empty;
    public int SubscriptionId { get; set; }

    public bool SnapshotFollows { get; set; }
    public int StartIndex { get; set; }
    public int TotalCount { get; set; }
    public bool IsPartial { get; set; }
    public IReadOnlyList<string>? Fields { get; set; }

    public int RowNumber { get; set; }
    public IReadOnlyDictionary<string, string?>? Row { get; set; }

    public string? RowId { get; set; }
    public int Position { get; set; }
    public IReadOnlyDictionary<string, string?>? ChangedFields { get; set; }

    public string? Reason { get; set; }
    public string? Message { get; set; }
}
