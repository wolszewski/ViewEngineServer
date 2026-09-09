using System.Text.Json;
using NBomber.WebSockets;
using ViewEngineServer.WebApp.WebSocket.Dto;

namespace LiveViewEngine.WebHost.LoadTests.Protocol;

/// <summary>
/// Thin JSON send/receive helper over NBomber's WebSocket wrapper, speaking the same
/// subscribe/updateview/unsubscribe wire contract as LiveViewEngine.WebHost (see
/// docs/subscription-design.md). Always uses messageFormat "json" for simple parsing.
/// </summary>
public sealed class WsProtocolClient(WebSocket socket) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public Task Connect(string url, CancellationToken ct = default) => socket.Connect(url, ct);

    public Task Close(CancellationToken ct = default) =>
        socket.Close(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, ct);

    public ValueTask SendCommand(WsInboundMessage command, CancellationToken ct = default)
    {
        command.MessageFormat ??= "json";
        var json = JsonSerializer.Serialize(command, JsonOptions);
        return socket.Send(json, ct);
    }

    public async Task<WsServerMessage> ReceiveMessage(CancellationToken ct = default)
    {
        using var response = await socket.Receive(ct);
        var message = JsonSerializer.Deserialize<WsServerMessage>(response.Data.Span, JsonOptions);
        return message ?? throw new InvalidOperationException("Received an empty/invalid WS message.");
    }

    /// <summary>
    /// Sends a subscribe command and pumps received frames until the full snapshot stream
    /// (subscriptionAccepted, snapshotStart, snapshotRow*, eos) completes for that subscription.
    /// Returns the number of rows observed in the snapshot.
    /// </summary>
    public async Task<int> SubscribeAndAwaitSnapshot(WsInboundMessage subscribeCommand, CancellationToken ct = default)
    {
        await SendCommand(subscribeCommand, ct);

        var rowCount = 0;
        while (true)
        {
            var message = await ReceiveMessage(ct);
            switch (message.Type)
            {
                case "subscriptionRejected":
                    throw new InvalidOperationException(
                        $"Subscription rejected: {message.Reason} - {message.Message}");
                case "snapshotRow":
                    rowCount++;
                    break;
                case "eos":
                    return rowCount;
            }
        }
    }

    public void Dispose() => socket.Dispose();
}
