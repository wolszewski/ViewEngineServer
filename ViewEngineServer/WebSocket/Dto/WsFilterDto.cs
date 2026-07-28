namespace ViewEngineServer.WebSocket.Dto;

public sealed class WsFilterDto
{
    public string Field { get; set; } = string.Empty;

    public string Operator { get; set; } = "eq";
    public object? Value { get; set; }
}