using System.Collections.Concurrent;
using System.Threading.Channels;
using LiveViewEngine.WebHost.LoadTests.Config;
using LiveViewEngine.WebHost.LoadTests.Ingest;
using LiveViewEngine.WebHost.LoadTests.Protocol;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.WebSockets;
using ViewEngineServer.WebApp.WebSocket.Dto;

namespace LiveViewEngine.WebHost.LoadTests.Scenarios;

/// <summary>
/// Measures broadcast delta latency: a single background updater ticks at
/// LoadTestSettings.DeltaUpdatesPerSecond, rewriting DeltaUpdatedAtField (a UTC timestamp) on one
/// row in DeltaCollectionName. The row also carries DeltaCreatedAtField, set once on insert and
/// never touched again, so the row realistically models created-vs-updated semantics.
/// DeltaSubscriberCount virtual users each keep one persistent WS subscription open (opened lazily
/// on that VU's first iteration, reused across iterations) and, every iteration, wait for the next
/// broadcast rowUpdate and report customLatencyMs = receivedAt - parse(DeltaUpdatedAtField). Update
/// rate is independent of subscriber count/iteration speed - it's the one shared stream all
/// subscribers observe.
/// </summary>
public static class DeltaLatencyScenario
{
    private const string RowKey = "row-1";

    public static ScenarioProps Create(LoadTestSettings settings, IIngestClient ingestClient)
    {
        var subscribers = new ConcurrentDictionary<string, Channel<double>>();
        var subscriberSockets = new ConcurrentBag<(WebSocket Socket, CancellationTokenSource Cts, Task ReadLoop)>();
        CancellationTokenSource? updaterCts = null;
        Task? updaterLoop = null;

        return Scenario.Create("delta_latency", async context =>
        {
            var channel = await GetOrCreateSubscription(context, settings, subscribers, subscriberSockets);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(settings.DeltaAwaitTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                context.ScenarioCancellationToken, timeoutCts.Token);

            try
            {
                var latencyMs = await channel.Reader.ReadAsync(linkedCts.Token);
                return Response.Ok(customLatencyMs: latencyMs);
            }
            catch (OperationCanceledException) when (context.ScenarioCancellationToken.IsCancellationRequested)
            {
                // Run is shutting down (KeepConstant duration elapsed) - not a real "no update
                // observed" failure, so drop this iteration from stats entirely instead of counting
                // it as a fail.
                throw new IgnoreMeasurementException();
            }
            catch (OperationCanceledException)
            {
                return Response.Fail(message: "Timed out waiting for broadcast update.");
            }
        })
        .WithInit(async initContext =>
        {
            var created = await ingestClient.CreateCollectionAsync(
                settings.DeltaCollectionName,
                [settings.DeltaCreatedAtField, settings.DeltaUpdatedAtField],
                ["string", "string"],
                CancellationToken.None);
            if (!created)
            {
                initContext.Logger.Warning(
                    "delta_latency: could not create collection '{Collection}'. Continuing with the assumption it already exists.",
                    settings.DeltaCollectionName);
            }

            // Insert the single row before subscribers connect, so every observed message afterwards
            // is a rowUpdate (ChangedFields) rather than a rowInsert on first subscribe. Both fields
            // are set here, matching row-creation semantics; only DeltaUpdatedAtField is rewritten
            // on subsequent update ticks.
            var now = DateTimeOffset.UtcNow.ToString("O");
            await ingestClient.IngestAsync(
                settings.DeltaCollectionName,
                RowKey,
                new Dictionary<string, string?>
                {
                    [settings.DeltaCreatedAtField] = now,
                    [settings.DeltaUpdatedAtField] = now
                },
                CancellationToken.None);

            updaterCts = new CancellationTokenSource();
            updaterLoop = RunUpdaterAsync(settings, ingestClient, updaterCts.Token);

            initContext.Logger.Information(
                "delta_latency: background updater writing '{Field}' at {Rate}/s on '{Collection}'.",
                settings.DeltaUpdatedAtField,
                settings.DeltaUpdatesPerSecond,
                settings.DeltaCollectionName);
        })
        .WithClean(async _ =>
        {
            updaterCts?.Cancel();
            if (updaterLoop is not null)
            {
                try
                {
                    await updaterLoop;
                }
                catch (OperationCanceledException) { /* expected on shutdown */ }
            }

            foreach (var (socket, cts, readLoop) in subscriberSockets)
            {
                cts.Cancel();
                try
                {
                    await readLoop;
                }
                catch (OperationCanceledException) { /* expected on shutdown */ }

                socket.Dispose();
                cts.Dispose();
            }

            subscribers.Clear();
        })
        .WithLoadSimulations(
            Simulation.KeepConstant(
                copies: settings.DeltaSubscriberCount,
                during: TimeSpan.FromSeconds(settings.DeltaDurationSeconds)));
    }

    private static async Task<Channel<double>> GetOrCreateSubscription(
        IScenarioContext context,
        LoadTestSettings settings,
        ConcurrentDictionary<string, Channel<double>> subscribers,
        ConcurrentBag<(WebSocket Socket, CancellationTokenSource Cts, Task ReadLoop)> subscriberSockets)
    {
        var instanceId = context.ScenarioInfo.InstanceId;
        if (subscribers.TryGetValue(instanceId, out var existing))
        {
            return existing;
        }

        var channel = Channel.CreateBounded<double>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        if (!subscribers.TryAdd(instanceId, channel))
        {
            return subscribers[instanceId];
        }

        var socket = new WebSocket(new WebSocketConfig());
        var protocol = new WsProtocolClient(socket);
        await protocol.Connect(settings.WebSocketUrl, context.ScenarioCancellationToken);
        await protocol.SendCommand(
            new WsInboundMessage
            {
                Type = "subscribe",
                CollectionId = settings.DeltaCollectionName,
                SendSnapshot = false
            },
            context.ScenarioCancellationToken);

        var cts = new CancellationTokenSource();
        var readLoop = ReadBroadcastLoopAsync(protocol, settings.DeltaUpdatedAtField, channel, context.Logger, cts.Token);
        subscriberSockets.Add((socket, cts, readLoop));

        return channel;
    }

    private static async Task RunUpdaterAsync(LoadTestSettings settings, IIngestClient ingestClient, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / settings.DeltaUpdatesPerSecond));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // Only DeltaUpdatedAtField is sent - AddOrUpdate applies a partial patch, so
                // DeltaCreatedAtField (set once on insert above) is left untouched.
                await ingestClient.IngestAsync(
                    settings.DeltaCollectionName,
                    RowKey,
                    new Dictionary<string, string?> { [settings.DeltaUpdatedAtField] = DateTimeOffset.UtcNow.ToString("O") },
                    ct);
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }

    private static async Task ReadBroadcastLoopAsync(
        WsProtocolClient protocol,
        string updatedAtField,
        Channel<double> channel,
        Serilog.ILogger logger,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            WsServerMessage message;
            try
            {
                message = await protocol.ReceiveMessage(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "delta_latency: WS read loop error, stopping.");
                return;
            }

            var rawValue = message.Type switch
            {
                "rowInsert" => message.Row?.GetValueOrDefault(updatedAtField),
                "rowUpdate" => message.ChangedFields?.GetValueOrDefault(updatedAtField),
                _ => null
            };

            if (rawValue is null || !DateTimeOffset.TryParse(rawValue, out var sentAt))
            {
                continue;
            }

            var latencyMs = (DateTimeOffset.UtcNow - sentAt).TotalMilliseconds;
            await channel.Writer.WriteAsync(latencyMs, ct);
        }
    }
}
