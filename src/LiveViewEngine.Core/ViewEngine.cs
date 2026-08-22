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
    private readonly ConcurrentDictionary<SubscriptionKey, SubscriptionRouteLock> _subscriptionRouteLocks = new();
    private bool _disposed;

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

    public Task<IngestResult> IngestAsync(IngestCommand command, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return ProcessIngestAsync(command, ct);
    }

    public Task<IReadOnlyList<ViewDelta>> SubscribeAsync(SubscriptionCommand command, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return ProcessSubscribeAsync(command, ct);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var routeLock in _subscriptionRouteLocks.Values)
        {
            routeLock.Dispose();
        }
        _subscriptionRouteLocks.Clear();

        foreach (var runtime in _collectionRuntimes.Values)
        {
            runtime.Dispose();
        }
    }

    private async Task<IngestResult> ProcessIngestAsync(IngestCommand command, CancellationToken ct)
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

            if (command is CreateFilterPresetCommand createFilterPreset)
            {
                return await runtime.EnqueueAsync(
                    new RegisterFilterPresetRuntimeWork(
                        runtime,
                        createFilterPreset.FilterPresetId,
                        createFilterPreset.Filters),
                    ct);
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

    private async Task<IReadOnlyList<ViewDelta>> ProcessSubscribeAsync(SubscriptionCommand command, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        string? metricsCollectionId = GetCollectionIdForSubscription(command);
        try
        {
            if (command is UnsubscribeCommand unsubscribe && unsubscribe.SubscriptionId == 0)
            {
                await UnsubscribeAllForConnectionAsync(unsubscribe.ConnectionId, ct);
                return [];
            }

            return await ExecuteWithSubscriptionRouteLockAsync(
                command.EffectiveSubscriptionKey,
                ct,
                async () =>
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
                        CollectionRuntime? previousRuntime = null;
                        bool isCrossCollectionReplacement =
                            _subscriptionRoutes.TryGetValue(subscriptionKey, out var previousCollectionId) &&
                            !string.Equals(previousCollectionId, subscribeCollectionId, StringComparison.Ordinal) &&
                            _collectionRuntimes.TryGetValue(previousCollectionId, out previousRuntime);

                        var deltas = await subscribeRuntime.EnqueueAsync(
                            new SubscribeRuntimeWork(subscribeRuntime, subscribe),
                            ct);
                        if (subscribeRuntime.ContainsSubscription(subscriptionKey))
                        {
                            if (isCrossCollectionReplacement)
                            {
                                await previousRuntime!.EnqueueAsync(
                                    new UnsubscribeRuntimeWork(previousRuntime, new UnsubscribeCommand
                                    {
                                        ConnectionId = subscribe.ConnectionId,
                                        SubscriptionId = subscribe.SubscriptionId
                                    }),
                                    ct);
                            }

                            _subscriptionRoutes[subscriptionKey] = subscribeCollectionId;
                        }
                        else
                        {
                            _subscriptionRoutes.TryRemove(subscriptionKey, out _);
                        }

                        return deltas;
                    }

                    if (command is UnsubscribeCommand unsubscribeCommand)
                    {
                        var key = unsubscribeCommand.EffectiveSubscriptionKey;
                        var collectionId = GetCollectionIdForSubscription(command);
                        if (collectionId is null || !_collectionRuntimes.TryGetValue(collectionId, out var unsubRuntime))
                        {
                            _subscriptionRoutes.TryRemove(key, out _);
                        }
                        else
                        {
                            await unsubRuntime.EnqueueAsync(new UnsubscribeRuntimeWork(unsubRuntime, unsubscribeCommand), ct);
                            _subscriptionRoutes.TryRemove(key, out _);
                        }

                        return [];
                    }

                    if (command is UpdateViewCommand updateCommand)
                    {
                        var collectionId = GetCollectionIdForSubscription(updateCommand);
                        if (collectionId is null || !_collectionRuntimes.TryGetValue(collectionId, out var updateRuntime))
                        {
                            throw new InvalidOperationException(
                                $"Subscription '{updateCommand.EffectiveSubscriptionKey}' was not found.");
                        }

                        return await updateRuntime.EnqueueAsync(
                            new UpdateViewRuntimeWork(updateRuntime, updateCommand),
                            ct);
                    }

                    var nonUnsubCollectionId = GetCollectionIdForSubscription(command);
                    if (nonUnsubCollectionId is null ||
                        !_collectionRuntimes.TryGetValue(nonUnsubCollectionId, out var runtime))
                    {
                        return [];
                    }

                    RuntimeWorkItem<IReadOnlyList<ViewDelta>> work = new UnknownSubscriptionRuntimeWork(command);
                    return await runtime.EnqueueAsync(work, ct);
                });
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

    private async Task<T> ExecuteWithSubscriptionRouteLockAsync<T>(
        SubscriptionKey key,
        CancellationToken ct,
        Func<Task<T>> work)
    {
        var routeLock = _subscriptionRouteLocks.GetOrAdd(key, static _ => new SubscriptionRouteLock());
        routeLock.Claim();
        bool acquired = false;
        try
        {
            await routeLock.WaitSemaphoreAsync(ct);
            acquired = true;
            return await work();
        }
        finally
        {
            if (acquired)
            {
                routeLock.Release(key, _subscriptionRouteLocks);
            }
            else
            {
                routeLock.Unclaim(key, _subscriptionRouteLocks);
            }
        }
    }

    private sealed class SubscriptionRouteLock : IDisposable
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private int _activeUsers;

        public void Claim()
        {
            Interlocked.Increment(ref _activeUsers);
        }

        public async Task WaitSemaphoreAsync(CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        }

        public void Release(SubscriptionKey key, ConcurrentDictionary<SubscriptionKey, SubscriptionRouteLock> routeLocks)
        {
            _semaphore.Release();
            if (Interlocked.Decrement(ref _activeUsers) == 0)
            {
                routeLocks.TryRemove(new KeyValuePair<SubscriptionKey, SubscriptionRouteLock>(key, this));
            }
        }

        public void Unclaim(SubscriptionKey key, ConcurrentDictionary<SubscriptionKey, SubscriptionRouteLock> routeLocks)
        {
            if (Interlocked.Decrement(ref _activeUsers) == 0)
            {
                routeLocks.TryRemove(new KeyValuePair<SubscriptionKey, SubscriptionRouteLock>(key, this));
            }
        }

        public void Dispose()
        {
            _semaphore.Dispose();
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

        foreach (var subscriptionKey in _subscriptionRoutes.Keys.ToArray())
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

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ViewEngine));
        }
    }
}
