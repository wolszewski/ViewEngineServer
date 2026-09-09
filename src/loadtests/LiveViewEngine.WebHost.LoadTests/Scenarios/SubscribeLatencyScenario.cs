using LiveViewEngine.WebHost.LoadTests.Config;
using LiveViewEngine.WebHost.LoadTests.Protocol;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.WebSockets;
using ViewEngineServer.WebApp.WebSocket.Dto;

namespace LiveViewEngine.WebHost.LoadTests.Scenarios;

/// <summary>
/// Measures snapshot load latency: the "subscribe_snapshot" step times sending `subscribe`
/// against a collection pre-seeded with LoadTestSettings.SubscribeSeedRowCount rows (see
/// Program.cs / SnapshotSeeder) until the full snapshot stream completes
/// (subscriptionAccepted -> snapshotStart -> snapshotRow* -> eos). Connection setup is timed
/// separately by the "connect" step so it doesn't skew the snapshot-load numbers.
/// </summary>
public static class SubscribeLatencyScenario
{
    public static ScenarioProps Create(LoadTestSettings settings)
    {
        return Scenario.Create("subscribe_latency", async context =>
        {
            using var socket = new WebSocket(new WebSocketConfig());
            using var protocol = new WsProtocolClient(socket);

            var connect = await Step.Run("connect", context, async () =>
            {
                await protocol.Connect(settings.WebSocketUrl, context.ScenarioCancellationToken);
                return Response.Ok();
            });

            IResponse result = connect;
            if (!connect.IsError)
            {
                result = await Step.Run("subscribe_snapshot", context, async () =>
                {
                    var rowCount = await protocol.SubscribeAndAwaitSnapshot(
                        new WsInboundMessage
                        {
                            Type = "subscribe",
                            CollectionId = settings.CollectionName,
                            SendSnapshot = true,
                            StartIndex = 0,
                            PageSize = settings.SubscribeSnapshotPageSize
                        },
                        context.ScenarioCancellationToken);

                    return Response.Ok(sizeBytes: rowCount);
                });
            }

            await CloseQuietly(protocol, context.ScenarioCancellationToken);
            return result;
        })
        .WithLoadSimulations(
            Simulation.KeepConstant(
                copies: settings.SubscribeCopies,
                during: TimeSpan.FromSeconds(settings.SubscribeDurationSeconds)));
    }

    /// <summary>
    /// Closing after we've already read the full snapshot is best-effort cleanup, not part of the
    /// measured latency: under load the server may already have torn down the socket, so
    /// ClientWebSocket's close handshake can throw (mirrors the same abrupt-close tolerance in
    /// WebSocketSessionManager on the server side). Letting that propagate here would surface as a
    /// spurious "unhandled exception" in NBomber's log for an otherwise-successful iteration.
    /// </summary>
    private static async Task CloseQuietly(WsProtocolClient protocol, CancellationToken ct)
    {
        try
        {
            await protocol.Close(ct);
        }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException
            or OperationCanceledException
            or ObjectDisposedException)
        {
        }
    }
}
