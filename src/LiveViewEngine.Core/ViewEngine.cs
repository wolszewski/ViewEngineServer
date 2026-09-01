using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.DataIngest;
using LiveViewEngine.Core.Runtime;
using LiveViewEngine.Core.Runtime.WorkEvents;
using LiveViewEngine.Core.Views;
using Microsoft.Extensions.Logging;

namespace LiveViewEngine.Core;

public interface IViewEngine
{
    Task<IngestResult> IngestAsync(IngestCommand command, CancellationToken ct = default);
    Task<IReadOnlyList<ViewDelta>> SubscribeAsync(SubscriptionCommand command, CancellationToken ct = default);
    Task<IReadOnlyList<ViewDelta>> SubscribeAsync(SubscriptionCommand command, Action? onBeforeProcess, CancellationToken ct = default);
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
    private readonly ConcurrentDictionary<SubscriptionKey, SubscribeCommand> _pendingSubscribes = new();
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
        return ProcessSubscribeAsync(command, null, ct);
    }

    public Task<IReadOnlyList<ViewDelta>> SubscribeAsync(SubscriptionCommand command, Action? onBeforeProcess, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return ProcessSubscribeAsync(command, onBeforeProcess, ct);
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
                return await HandleCreateCollectionAsync(create, ct);
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
                UpsertRowCommand upsert => new UpsertRuntimeWork(
                    runtime,
                    upsert,
                    result => PublishMutationResultAsync(result, ct)),
                DeleteRowCommand delete => new DeleteRuntimeWork(
                    runtime,
                    delete,
                    result => PublishMutationResultAsync(result, ct)),
                _ => new UnknownCommandRuntimeWork(command),
            };

            var mutationResult = await runtime.EnqueueAsync(work, ct);
            return mutationResult.Result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error processing ingest command for collection '{CollectionId}'.",
                command.CollectionId);
            return IngestResult.Fail(ex.Message);
        }
    }

    private async Task<IReadOnlyList<ViewDelta>> ProcessSubscribeAsync(SubscriptionCommand command,
        Action? onBeforeProcess,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var metricsCollectionId = GetCollectionIdForSubscription(command);

        if (command is UnsubscribeCommand { SubscriptionId: 0 } unsubscribe)
        {
            await UnsubscribeAllForConnectionAsync(unsubscribe.ConnectionId, ct);
            return [];
        }

        var result = await ExecuteWithSubscriptionRouteLockAsync(
            command.EffectiveSubscriptionKey,
            ct,
            async () =>
            {
                return command switch
                {
                    SubscribeCommand subscribe => await HandleSubscribeCommandAsync(subscribe, ct),
                    UnsubscribeCommand unsubscribeCommand => await HandleUnsubscribeCommandAsync(unsubscribeCommand, ct),
                    UpdateViewCommand updateCommand => await HandleUpdateViewCommandAsync(updateCommand, onBeforeProcess, ct),
                    _ => await HandleUnknownSubscriptionCommandAsync(command, ct)
                };
            });

        var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (metricsCollectionId is not null)
        {
            _metrics?.RecordSubscriptionDuration(durationMs, command.GetType().Name, metricsCollectionId);
        }

        return result;
    }

    private async ValueTask PublishMutationResultAsync(MutationResult mutationResult, CancellationToken ct)
    {
        if (mutationResult.Groups is not { Count: > 0 })
        {
            return;
        }

        foreach (var group in mutationResult.Groups)
        {
            await _publisher.PublishAsync(group.Targets, group.Deltas, ct).ConfigureAwait(false);
        }

        await _publisher.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ViewDelta>> HandleSubscribeCommandAsync(SubscribeCommand subscribe,
        CancellationToken ct)
    {
        var subscribeCollectionId = subscribe.View.CollectionId;
        if (!_collectionRuntimes.TryGetValue(subscribeCollectionId, out var subscribeRuntime))
        {
            // Remember the request so a later updateview retry can resume it once the collection exists.
            _pendingSubscribes[subscribe.EffectiveSubscriptionKey] = subscribe;
            return [];
        }

        var subscriptionKey = subscribe.EffectiveSubscriptionKey;
        _pendingSubscribes.TryRemove(subscriptionKey, out _);
        if (_subscriptionRoutes.TryGetValue(subscriptionKey, out var previousCollectionId) &&
            !string.Equals(previousCollectionId, subscribeCollectionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Subscription '{subscriptionKey}' is already bound to collection '{previousCollectionId}'. " +
                "A subscription cannot switch collections. Create a new subscription id instead.");
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

    private async Task<IReadOnlyList<ViewDelta>> HandleUnsubscribeCommandAsync(UnsubscribeCommand unsubscribeCommand,
        CancellationToken ct)
    {
        var key = unsubscribeCommand.EffectiveSubscriptionKey;
        _pendingSubscribes.TryRemove(key, out _);
        var collectionId = GetCollectionIdForSubscription(unsubscribeCommand);
        if (collectionId is null || !_collectionRuntimes.TryGetValue(collectionId, out var unsubRuntime))
        {
            _subscriptionRoutes.TryRemove(key, out _);
            return [];
        }

        await unsubRuntime.EnqueueAsync(new UnsubscribeRuntimeWork(unsubRuntime, unsubscribeCommand), ct);
        _subscriptionRoutes.TryRemove(key, out _);
        return [];
    }

    private async Task<IReadOnlyList<ViewDelta>> HandleUpdateViewCommandAsync(UpdateViewCommand updateCommand,
        Action? onBeforeProcess,
        CancellationToken ct)
    {
        var collectionId = GetCollectionIdForSubscription(updateCommand);
        if (collectionId is null || !_collectionRuntimes.TryGetValue(collectionId, out var updateRuntime))
        {
            // The subscription may have been accepted before its collection existed (client is retrying
            // while waiting for the collection to be created). If we still remember the original
            // subscribe request and the collection now exists, resume it instead of no-op'ing forever.
            if (_pendingSubscribes.TryGetValue(updateCommand.EffectiveSubscriptionKey, out var pendingSubscribe)
                && _collectionRuntimes.ContainsKey(pendingSubscribe.View.CollectionId))
            {
                var resumedSubscribe = new SubscribeCommand
                {
                    ConnectionId = pendingSubscribe.ConnectionId,
                    SubscriptionId = pendingSubscribe.SubscriptionId,
                    View = new ViewDefinition
                    {
                        CollectionId = pendingSubscribe.View.CollectionId,
                        FilterPresetId = pendingSubscribe.View.FilterPresetId,
                        SortColumn = updateCommand.SortColumn ?? pendingSubscribe.View.SortColumn,
                        SortAscending = updateCommand.SortAscending ?? pendingSubscribe.View.SortAscending,
                        Filters = updateCommand.Filters ?? pendingSubscribe.View.Filters,
                        Fields = updateCommand.Fields ?? pendingSubscribe.View.Fields
                    },
                    StartIndex = updateCommand.StartIndex ?? pendingSubscribe.StartIndex,
                    PageSize = updateCommand.PageSize ?? pendingSubscribe.PageSize,
                    SendSnapshot = pendingSubscribe.SendSnapshot
                };

                return await HandleSubscribeCommandAsync(resumedSubscribe, ct);
            }

            // Still waiting for the collection to be created — treat as a no-op rather than an error.
            return [];
        }

        return await updateRuntime.EnqueueAsync(
            new UpdateViewRuntimeWork(updateRuntime, updateCommand, onBeforeProcess),
            ct);
    }

    private async Task<IReadOnlyList<ViewDelta>> HandleUnknownSubscriptionCommandAsync(SubscriptionCommand command,
        CancellationToken ct)
    {
        var collectionId = GetCollectionIdForSubscription(command);
        if (collectionId is null || !_collectionRuntimes.TryGetValue(collectionId, out var runtime))
        {
            return [];
        }

        RuntimeWorkItem<IReadOnlyList<ViewDelta>> work = new UnknownSubscriptionRuntimeWork(command);
        return await runtime.EnqueueAsync(work, ct);
    }

    private async Task<T> ExecuteWithSubscriptionRouteLockAsync<T>(
        SubscriptionKey key,
        CancellationToken ct,
        Func<Task<T>> work)
    {
        var routeLock = _subscriptionRouteLocks.GetOrAdd(key, static _ => new SubscriptionRouteLock());
        routeLock.Claim();
        var acquired = false;
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

        public void Release(SubscriptionKey key,
            ConcurrentDictionary<SubscriptionKey, SubscriptionRouteLock> routeLocks)
        {
            _semaphore.Release();
            if (Interlocked.Decrement(ref _activeUsers) == 0)
            {
                routeLocks.TryRemove(new KeyValuePair<SubscriptionKey, SubscriptionRouteLock>(key, this));
            }
        }

        public void Unclaim(SubscriptionKey key,
            ConcurrentDictionary<SubscriptionKey, SubscriptionRouteLock> routeLocks)
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
        if (command is SubscribeCommand subscribeCommand)
        {
            return subscribeCommand.View.CollectionId;
        }

        return _subscriptionRoutes.GetValueOrDefault(command.EffectiveSubscriptionKey);
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

        foreach (var subscriptionKey in _pendingSubscribes.Keys.ToArray())
        {
            if (subscriptionKey.ConnectionId == connectionId)
            {
                _pendingSubscribes.TryRemove(subscriptionKey, out _);
            }
        }
    }

    private async Task<IngestResult> HandleCreateCollectionAsync(CreateCollectionCommand command, CancellationToken ct)
    {
        if (!_store.TryCreateCollection(command.Schema))
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

        await ResumePendingSubscribesAsync(command.CollectionId, ct).ConfigureAwait(false);

        return IngestResult.Ok();
    }

    private async Task ResumePendingSubscribesAsync(string collectionId, CancellationToken ct)
    {
        var toResume = _pendingSubscribes.Values
            .Where(pending => string.Equals(pending.View.CollectionId, collectionId, StringComparison.Ordinal))
            .ToArray();

        foreach (var pendingSubscribe in toResume)
        {
            IReadOnlyList<ViewDelta> deltas;
            try
            {
                deltas = await ExecuteWithSubscriptionRouteLockAsync(
                    pendingSubscribe.EffectiveSubscriptionKey,
                    ct,
                    () => HandleSubscribeCommandAsync(pendingSubscribe, ct)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "Error resuming pending subscription for connection {ConnectionId}, subscription {SubscriptionId} on collection '{CollectionId}'.",
                    pendingSubscribe.ConnectionId, pendingSubscribe.SubscriptionId, collectionId);
                continue;
            }

            if (deltas.Count == 0)
            {
                continue;
            }

            await _publisher.PublishAsync(
                [new SubscriberTarget(pendingSubscribe.ConnectionId, pendingSubscribe.SubscriptionId)],
                deltas,
                ct).ConfigureAwait(false);
        }

        if (toResume.Length > 0)
        {
            await _publisher.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ViewEngine));
        }
    }
}