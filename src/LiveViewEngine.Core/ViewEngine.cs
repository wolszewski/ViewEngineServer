using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
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
    private readonly ConcurrentDictionary<SubscriptionKey, string> _subscriptionRoutes = new();

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

        metrics?.RegisterSortIndexGaugeSource(() => _collectionRuntimes.Values.Sum(static r => r.SortIndexCount));

        metrics?.RegisterTypedColumnGaugeSource(() =>
            _collectionRuntimes.Values
                .SelectMany(static r => r.GetActiveTypedColumns())
                .Select(static x => new Measurement<int>(
                    x.RefCount,
                    new KeyValuePair<string, object?>("collectionId", x.CollectionId),
                    new KeyValuePair<string, object?>("fieldName", x.FieldName))));

        metrics?.RegisterCollectionQueueDepthGaugeSource(() =>
            _collectionRuntimes.Values.Select(static r => new Measurement<int>(
                r.WorkerQueueLength,
                new KeyValuePair<string, object?>("collectionId", r.Collection.Schema.CollectionName))));
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

            if (command is CreateFilterPresetCommand createSegment)
            {
                return await runtime.EnqueueAsync(
                    new RegisterSegmentRuntimeWork(runtime, createSegment.FilterPresetId, createSegment.Filters), ct);
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
                    await _publisher.PublishAsync(group.Targets, group.Deltas, ct);
                }

                await _publisher.FlushAsync(ct);
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
        var started = Stopwatch.GetTimestamp();
        string? metricsCollectionId = GetCollectionIdForSubscription(command);
        try
        {
            if (command is SubscribeCommand subscribe)
            {
                metricsCollectionId = subscribe.View.CollectionId;
                string subscribeCollectionId = subscribe.View.CollectionId;
                if (!_collectionRuntimes.TryGetValue(subscribeCollectionId, out var subscribeRuntime))
                {
                    return [];
                }

                var subscriptionKey = subscribe.EffectiveSubscriptionKey;
                if (_subscriptionRoutes.TryGetValue(subscriptionKey, out var previousCollectionId) &&
                    !string.Equals(previousCollectionId, subscribeCollectionId, StringComparison.Ordinal) &&
                    _collectionRuntimes.TryGetValue(previousCollectionId, out var previousRuntime))
                {
                    await previousRuntime.EnqueueAsync(
                        new UnsubscribeRuntimeWork(previousRuntime, new UnsubscribeCommand
                        {
                            ConnectionId = subscribe.ConnectionId,
                            SubscriptionId = subscribe.SubscriptionId
                        }),
                        ct);
                }

                var deltas = await subscribeRuntime.EnqueueAsync(new SubscribeRuntimeWork(subscribeRuntime, subscribe), ct);
                if (subscribeRuntime.ContainsSubscription(subscriptionKey))
                {
                    _subscriptionRoutes[subscriptionKey] = subscribeCollectionId;
                }
                else
                {
                    _subscriptionRoutes.TryRemove(subscriptionKey, out _);
                }

                return deltas;
            }

            if (command is UnsubscribeCommand unsubscribe && unsubscribe.SubscriptionId == 0)
            {
                await UnsubscribeAllForConnectionAsync(unsubscribe.ConnectionId, ct);
                return [];
            }

            var collectionId = GetCollectionIdForSubscription(command);
            if (collectionId is null || !_collectionRuntimes.TryGetValue(collectionId, out var runtime))
            {
                if (command is UnsubscribeCommand unmatchedUnsubscribe)
                {
                    _subscriptionRoutes.TryRemove(unmatchedUnsubscribe.EffectiveSubscriptionKey, out _);
                }

                return [];
            }

            RuntimeWorkItem<IReadOnlyList<ViewDelta>> work = command switch
            {
                ChangeViewportCommand change => new ChangeViewportRuntimeWork(runtime, change),
                UnsubscribeCommand unsub => new UnsubscribeRuntimeWork(runtime, unsub),
                _ => new UnknownSubscriptionRuntimeWork(command),
            };

            var result = await runtime.EnqueueAsync(work, ct);
            if (command is UnsubscribeCommand unsubscribeCommand)
            {
                _subscriptionRoutes.TryRemove(unsubscribeCommand.EffectiveSubscriptionKey, out _);
            }

            return result;
        }
        finally
        {
            var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (metricsCollectionId is not null)
            {
                _metrics?.RecordSubscriptionDuration(durationMs, command.GetType().Name, metricsCollectionId);
            }
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

        return _subscriptionRoutes.TryGetValue(command.EffectiveSubscriptionKey, out var collectionId)
            ? collectionId
            : null;
    }

    private async Task UnsubscribeAllForConnectionAsync(int connectionId, CancellationToken ct)
    {
        foreach (var runtime in _collectionRuntimes.Values)
        {
            if (!runtime.TryGetCollectionIdForConnection(connectionId, out _))
            {
                continue;
            }

            await runtime.EnqueueAsync(
                new UnsubscribeRuntimeWork(runtime, new UnsubscribeCommand { ConnectionId = connectionId }),
                ct);
        }

        foreach (var subscriptionKey in _subscriptionRoutes.Keys)
        {
            if (subscriptionKey.ConnectionId == connectionId)
            {
                _subscriptionRoutes.TryRemove(subscriptionKey, out _);
            }
        }
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
