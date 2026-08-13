using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using LiveViewEngine.Core.Data;
using Microsoft.Extensions.Logging;

namespace LiveViewEngine.Core;

internal static class ViewEngineMetrics
{
    private static readonly Meter Meter = new("ViewEngineServer");

    internal static readonly Histogram<double> InsertDuration = Meter.CreateHistogram<double>(
        "viewengine.insert.duration",
        unit: "ms",
        description: "Time spent processing insert operations.");

    internal static readonly Histogram<double> UpdateDuration = Meter.CreateHistogram<double>(
        "viewengine.update.duration",
        unit: "ms",
        description: "Time spent processing update operations.");

    internal static readonly Histogram<double> SubscriptionDuration = Meter.CreateHistogram<double>(
        "viewengine.subscription.duration",
        unit: "ms",
        description: "Time spent processing subscription operations.");
}

public interface IViewEngine
{
    Task<IngestResult> IngestAsync(IngestCommand command, CancellationToken ct = default);
    Task<IReadOnlyList<ViewDelta>> SubscribeAsync(SubscriptionCommand command, CancellationToken ct = default);
}

public sealed class ViewEngine(
    ICollectionStore store,
    IOutboundPublisher publisher,
    ILogger<ViewEngine> logger)
    : IViewEngine, IDisposable
{
    private readonly ConcurrentDictionary<string, CollectionRuntime> _collectionRuntimes = new();

    public async Task<IngestResult> IngestAsync(IngestCommand command, CancellationToken ct = default)
    {
        try
        {
            if (command is CreateCollectionCommand create)
            {
                return HandleCreateCollection(create);
            }

            if (!_collectionRuntimes.TryGetValue(command.CollectionId, out var runtime))
            {
                return IngestResult.Fail($"Collection '{command.CollectionId}' not found.");
            }

            var queued = await runtime.EnqueueAsync(() => command switch
            {
                UpsertRowCommand upsert => runtime.HandleUpsert(upsert),
                DeleteRowCommand delete => runtime.HandleDelete(delete),
                _ => (IngestResult.Fail($"Unknown command type '{command.GetType().Name}'."), null)
            }, ct);

            if (queued.Groups is { Count: > 0 })
            {
                foreach (var (deltas, connectionIds) in queued.Groups)
                {
                    await publisher.PublishAsync(connectionIds, deltas, ct);
                }
            }

            return queued.Result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error processing ingest command for collection '{CollectionId}'.",
                command.CollectionId);
            return IngestResult.Fail(ex.Message);
        }
    }

    public async Task<IReadOnlyList<ViewDelta>> SubscribeAsync(
        SubscriptionCommand command, CancellationToken ct = default)
    {
        string? collectionId = GetCollectionIdForSubscription(command);

        if (collectionId is null || !_collectionRuntimes.TryGetValue(collectionId, out var runtime))
        {
            return [];
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            return await runtime.EnqueueAsync(() => command switch
            {
                SubscribeCommand sub => runtime.HandleSubscribe(sub),
                ChangeViewportCommand change => runtime.HandleChangeViewport(change),
                UnsubscribeCommand unsub => runtime.HandleUnsubscribe(unsub),
                _ => []
            }, ct);
        }
        finally
        {
            var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            ViewEngineMetrics.SubscriptionDuration.Record(
                durationMs,
                new KeyValuePair<string, object?>("commandType", command.GetType().Name),
                new KeyValuePair<string, object?>("collectionId", collectionId));
        }
    }

    public void Dispose()
    {
        foreach (var runtime in _collectionRuntimes.Values)
        {
            runtime.Dispose();
        }
    }

    private string? GetCollectionIdForSubscription(SubscriptionCommand command)
    {
        if (command is SubscribeCommand sub)
        {
            return sub.View.CollectionId;
        }

        foreach (var runtime in _collectionRuntimes.Values)
        {
            if (runtime.TryGetCollectionIdForConnection(command.ConnectionId, out var collectionId))
            {
                return collectionId;
            }
        }

        return null;
    }

    private IngestResult HandleCreateCollection(CreateCollectionCommand command)
    {
        if (!store.TryCreate(command.Schema))
        {
            return IngestResult.Fail(
                $"Collection '{command.CollectionId}' already exists.");
        }

        if (!store.TryGetRuntime(command.CollectionId, out var runtime) || runtime is null)
        {
            return IngestResult.Fail($"Collection '{command.CollectionId}' could not be initialized.");
        }

        _collectionRuntimes.TryAdd(command.CollectionId, runtime);

        logger.LogInformation("Collection '{CollectionId}' created ({FieldCount} fields).",
            command.CollectionId, command.Schema.Fields.Count);
        return IngestResult.Ok();
    }
}
