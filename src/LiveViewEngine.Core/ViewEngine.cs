using System.Collections.Concurrent;
using System.Diagnostics;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace LiveViewEngine.Core;

public interface IViewEngine
{
    Task<IngestResult> IngestAsync(IngestCommand command, CancellationToken ct = default);
    Task<IReadOnlyList<ViewDelta>> SubscribeAsync(SubscriptionCommand command, CancellationToken ct = default);
}

public sealed class ViewEngine : IViewEngine, IDisposable
{
    private readonly ICollectionStore _store;
    private readonly IOutboundPublisher _publisher;
    private readonly ILogger<ViewEngine> _logger;
    private readonly IViewEngineMetrics? _metrics;
    private readonly ConcurrentDictionary<string, CollectionRuntime> _collectionRuntimes = new();

    public ViewEngine(
        ICollectionStore store,
        IOutboundPublisher publisher,
        ILogger<ViewEngine> logger,
        IViewEngineMetrics? metrics)
    {
        _store = store;
        _publisher = publisher;
        _logger = logger;
        _metrics = metrics;

        metrics?.RegisterGaugeSources(
            () => _collectionRuntimes.Values.Sum(static r => r.ActiveSubscriptionCount),
            () => _collectionRuntimes.Values.Sum(static r => r.ActiveSharedViewCount),
            () => _collectionRuntimes.Values.Sum(static r => r.SortIndexCount));
    }

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
            
            RuntimeWorkItem<MutationResult> work = command switch
            {
                UpsertRowCommand upsert => new UpsertRuntimeWork(runtime, upsert),
                DeleteRowCommand delete => new DeleteRuntimeWork(runtime, delete),
                _ => new UnknownCommandRuntimeWork(command),
            };

            var mutationResult = await runtime.EnqueueAsync(work, ct);

            if (mutationResult.Groups is { Count: > 0 })
            {
                foreach (var group in mutationResult.Groups)
                {
                    await _publisher.PublishAsync(group.ConnectionIds, group.Deltas, ct);
                }
            }

            return mutationResult.Result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error processing ingest command for collection '{CollectionId}'.",
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
            RuntimeWorkItem<IReadOnlyList<ViewDelta>> work = command switch
            {
                SubscribeCommand sub => new SubscribeRuntimeWork(runtime, sub),
                ChangeViewportCommand change => new ChangeViewportRuntimeWork(runtime, change),
                UnsubscribeCommand unsub => new UnsubscribeRuntimeWork(runtime, unsub),
                _ => new UnknownSubscriptionRuntimeWork(command),
            };

            return await runtime.EnqueueAsync(work, ct);
        }
        finally
        {
            var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _metrics?.RecordSubscriptionDuration(durationMs, command.GetType().Name, collectionId!);
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
        if (!_store.TryCreate(command.Schema))
        {
            return IngestResult.Fail(
                $"Collection '{command.CollectionId}' already exists.");
        }

        if (!_store.TryGetRuntime(command.CollectionId, out var runtime) || runtime is null)
        {
            return IngestResult.Fail($"Collection '{command.CollectionId}' could not be initialized.");
        }

        _collectionRuntimes.TryAdd(command.CollectionId, runtime);

        _logger.LogInformation("Collection '{CollectionId}' created ({FieldCount} fields).",
            command.CollectionId, command.Schema.Fields.Count);
        return IngestResult.Ok();
    }

}
