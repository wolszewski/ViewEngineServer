namespace ViewEngineServer.WebApp.WebSocket.Dto;

public sealed class WsInboundMessage
{
    public string Type { get; set; } = string.Empty;
    public int? SubscriptionId { get; set; }

    public string? CollectionId { get; set; }
    public string? FieldPresetId { get; set; }
    public string? SortColumn { get; set; }
    public bool SortAscending { get; set; } = true;
    public List<WsFilterDto>? Filters { get; set; }
    public List<string>? Fields { get; set; }
    public int StartIndex { get; set; }
    public int? PageSize { get; set; }
    public bool? SendSnapshot { get; set; }
    public string? MessageFormat { get; set; }
}