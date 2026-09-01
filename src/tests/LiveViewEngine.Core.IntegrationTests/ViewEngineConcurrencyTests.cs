using System.Collections.Concurrent;
using System.Reflection;
using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.DataIngest;
using LiveViewEngine.Core.Output;
using LiveViewEngine.Core.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveViewEngine.Core.IntegrationTests;

public class ViewEngineConcurrencyTests
{
    private static (ViewEngine engine, ThreadSafeCapturingPublisher publisher) CreateEngine()
    {
        var metrics = new ViewEngineMetrics();
        var store = new CollectionStore(metrics, new LiveViewEngineOptions { EagerIndexing = false });
        var publisher = new ThreadSafeCapturingPublisher();
        var engine = new ViewEngine(store, publisher, NullLogger<ViewEngine>.Instance, metrics);
        return (engine, publisher);
    }

    private static CollectionSchema TradesSchema() => new("trades", ["symbol", "price", "quantity"]);

    private static Task<IngestResult> CreateTrades(ViewEngine engine) =>
        engine.IngestAsync(new CreateCollectionCommand
        {
            CollectionId = "trades",
            Schema = TradesSchema()
        });

    private static Task<IngestResult> UpsertTrade(ViewEngine engine, string key, string symbol, string price) =>
        engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "trades",
            Key = key,
            Fields = new Dictionary<string, string?> { ["symbol"] = symbol, ["price"] = price, ["quantity"] = "10" }
        });

    [Fact]
    public async Task ConcurrentUpserts_AllRowsPresent()
    {
        var (engine, _) = CreateEngine();
        await CreateTrades(engine);

        const int tasks = 10;
        const int rowsPerTask = 10;

        var work = Enumerable.Range(0, tasks).Select(t => Task.Run(async () =>
        {
            for (int i = 0; i < rowsPerTask; i++)
            {
                var key = $"t{t:D3}-{i:D3}";
                var result = await UpsertTrade(engine, key, $"SYM{t}", (t * rowsPerTask + i).ToString());
                Assert.True(result.Success, result.Error);
            }
        }));

        await Task.WhenAll(work);

        var snapshot = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 99,
            View = new ViewDefinition { CollectionId = "trades" },
            StartIndex = 0,
            PageSize = tasks * rowsPerTask
        });

        var delta = snapshot.ToSnapshotDelta();
        Assert.Equal(tasks * rowsPerTask, delta.TotalCount);
    }

    [Fact]
    public async Task SubscribeDuringFastInsert_SnapshotIsConsistent()
    {
        var (engine, _) = CreateEngine();
        await CreateTrades(engine);

        const int totalRows = 200;

        // Insert half the rows before subscribing starts.
        for (int i = 0; i < totalRows / 2; i++)
        {
            await UpsertTrade(engine, $"t{i:D4}", "AAPL", i.ToString());
        }

        // Insert the second half concurrently with a subscribe.
        var insertTask = Task.Run(async () =>
        {
            for (int i = totalRows / 2; i < totalRows; i++)
            {
                await UpsertTrade(engine, $"t{i:D4}", "AAPL", i.ToString());
            }
        });

        var subscribeTask = Task.Run(async () =>
        {
            var result = await engine.SubscribeAsync(new SubscribeCommand
            {
                ConnectionId = 1,
                View = new ViewDefinition { CollectionId = "trades" },
                StartIndex = 0,
                PageSize = totalRows
            });
            return result;
        });

        await Task.WhenAll(insertTask, subscribeTask);

        var deltas = await subscribeTask;
        var snapshot = deltas.ToSnapshotDelta();

        // Snapshot must be internally consistent: every row index it claimed must be valid.
        Assert.True(snapshot.TotalCount >= totalRows / 2,
            $"Expected at least {totalRows / 2} rows but got {snapshot.TotalCount}");
        Assert.True(snapshot.Rows.Count <= snapshot.TotalCount,
            "Returned more rows than TotalCount");
        Assert.All(snapshot.Rows, row => Assert.NotNull(row[CollectionSchema.PrimaryKeyIndex]));
    }

    [Fact]
    public async Task SubscribeBeforeInsert_ReceivesInsertEventsForAllRows()
    {
        var (engine, publisher) = CreateEngine();
        await CreateTrades(engine);

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            View = new ViewDefinition { CollectionId = "trades" },
            StartIndex = 0,
            PageSize = 200
        });

        const int rowCount = 50;

        await Task.WhenAll(Enumerable.Range(0, rowCount).Select(i =>
            UpsertTrade(engine, $"t{i:D4}", "GOOG", i.ToString())));

        var inserts = publisher.EventsFor(1).OfType<RowInsertEvent>().ToList();
        Assert.Equal(rowCount, inserts.Count);
    }

    [Fact]
    public async Task ConcurrentSubscribers_AllReceiveInsertEvents()
    {
        var (engine, publisher) = CreateEngine();
        await CreateTrades(engine);

        const int subscribers = 5;
        const int rowCount = 20;

        // Subscribe all clients in parallel.
        await Task.WhenAll(Enumerable.Range(0, subscribers).Select(s =>
            engine.SubscribeAsync(new SubscribeCommand
            {
                ConnectionId = s + 1,
                View = new ViewDefinition { CollectionId = "trades" },
                StartIndex = 0,
                PageSize = rowCount
            })));

        // Insert rows concurrently.
        await Task.WhenAll(Enumerable.Range(0, rowCount).Select(i =>
            UpsertTrade(engine, $"t{i:D4}", "MSFT", i.ToString())));

        for (int s = 0; s < subscribers; s++)
        {
            var inserts = publisher.EventsFor(s + 1).OfType<RowInsertEvent>().ToList();
            Assert.Equal(rowCount, inserts.Count);
        }
    }

    [Fact]
    public async Task ConcurrentIngestAndSubscribe_NoExceptions()
    {
        var (engine, _) = CreateEngine();
        await CreateTrades(engine);

        const int iterations = 50;

        var ingestTask = Task.Run(async () =>
        {
            for (int i = 0; i < iterations; i++)
            {
                var result = await UpsertTrade(engine, $"t{i:D4}", "IBM", i.ToString());
                Assert.True(result.Success, result.Error);
            }
        });

        var subscribeTask = Task.Run(async () =>
        {
            for (int i = 0; i < iterations; i++)
            {
                var result = await engine.SubscribeAsync(new SubscribeCommand
                {
                    ConnectionId = i + 1,
                    View = new ViewDefinition { CollectionId = "trades" },
                    StartIndex = 0,
                    PageSize = 10
                });
                Assert.IsType<SnapshotStartDelta>(result[0]);
                Assert.IsType<EndOfSnapshotDelta>(result[^1]);
            }
        });

        // No exception expected.
        await Task.WhenAll(ingestTask, subscribeTask);
    }

    [Fact]
    public async Task UpdateViewBoundaryWaitsForEarlierIngestPublish()
    {
        var metrics = new ViewEngineMetrics();
        var store = new CollectionStore(metrics, new LiveViewEngineOptions { EagerIndexing = false });
        var publisher = new BlockingPublishPublisher();
        var engine = new ViewEngine(store, publisher, NullLogger<ViewEngine>.Instance, metrics);

        await CreateTrades(engine);
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = 1,
            SubscriptionId = 1,
            View = new ViewDefinition { CollectionId = "trades" },
            StartIndex = 0,
            PageSize = 10
        });

        var ingestTask = engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = "trades",
            Key = "t1",
            Fields = new Dictionary<string, string?> { ["symbol"] = "AAPL", ["price"] = "1", ["quantity"] = "10" }
        });

        await publisher.WaitForPublishAsync();

        var boundaryReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updateTask = engine.SubscribeAsync(
            new UpdateViewCommand
            {
                ConnectionId = 1,
                SubscriptionId = 1,
                StartIndex = 0,
                PageSize = 10,
                SnapshotMode = SnapshotMode.Delta
            },
            () => boundaryReached.TrySetResult());

        await Assert.ThrowsAsync<TimeoutException>(
            () => boundaryReached.Task.WaitAsync(TimeSpan.FromMilliseconds(100)));

        publisher.ReleasePublish();

        await Task.WhenAll(ingestTask, updateTask, boundaryReached.Task);
    }

    [Fact]
    public async Task SubscriptionRouteLocks_AreReclaimedAfterDisconnectChurn()
    {
        var (engine, _) = CreateEngine();
        await CreateTrades(engine);

        var locksField = typeof(ViewEngine).GetField("_subscriptionRouteLocks", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(locksField);

        var routeLocks = locksField!.GetValue(engine) as System.Collections.IDictionary;
        Assert.NotNull(routeLocks);

        for (int i = 0; i < 25; i++)
        {
            var connectionId = i + 1000;
            var subscriptionId = i + 1;

            await engine.SubscribeAsync(new SubscribeCommand
            {
                ConnectionId = connectionId,
                SubscriptionId = subscriptionId,
                View = new ViewDefinition { CollectionId = "trades" },
                StartIndex = 0,
                PageSize = 1
            });

            await engine.SubscribeAsync(new UnsubscribeCommand
            {
                ConnectionId = connectionId,
                SubscriptionId = subscriptionId
            });

            Assert.Empty(routeLocks!);
        }
    }

    // Thread-safe publisher for concurrency tests.
    private sealed class ThreadSafeCapturingPublisher : IOutboundPublisher
    {
        private readonly IOutboundEventFormatter _formatter = new JsonOutboundEventFormatter();
        private readonly ConcurrentBag<(int ConnectionId, IReadOnlyList<DeltaEvent> Events)> _published = [];

        public ValueTask PublishAsync(
            IReadOnlyList<SubscriberTarget> targets,
            IReadOnlyList<ViewDelta> deltas,
            CancellationToken ct = default)
        {
            foreach (var target in targets)
            {
                _published.Add((target.ConnectionId, _formatter.Format(deltas, target.SubscriptionId)));
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public IEnumerable<DeltaEvent> EventsFor(int connectionId) =>
            _published
                .Where(p => p.ConnectionId == connectionId)
                .SelectMany(p => p.Events);
    }

    private sealed class BlockingPublishPublisher : IOutboundPublisher
    {
        private readonly TaskCompletionSource _publishStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _publishRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask PublishAsync(
            IReadOnlyList<SubscriberTarget> targets,
            IReadOnlyList<ViewDelta> deltas,
            CancellationToken ct = default)
        {
            if (targets.Count > 0 && deltas.OfType<RowInsertDelta>().Any())
            {
                _publishStarted.TrySetResult();
                _publishRelease.Task.GetAwaiter().GetResult();
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public Task WaitForPublishAsync() => _publishStarted.Task;

        public void ReleasePublish() => _publishRelease.TrySetResult();
    }
}
