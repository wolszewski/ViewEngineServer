using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.Output;
using LiveViewEngine.Core.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveViewEngine.Core.IntegrationTests;

public class ViewEngineBlotterTests
{
    private const string CollectionId = "objects";
    private static readonly string[] Categories = { "category1", "category2", "category3", "category4" };

    private static (ViewEngine engine, CapturingPublisher publisher, ICollectionStore store) CreateEngine()
    {
        var metrics = new ViewEngineMetrics();
        var store = new CollectionStore(metrics);
        var publisher = new CapturingPublisher();
        var engine = new ViewEngine(store, publisher, NullLogger<ViewEngine>.Instance, metrics);
        return (engine, publisher, store);
    }

    private static CollectionSchema ObjectsSchema() => new(CollectionId, new[]
    {
        "id", "date", "category", "f01", "f02", "f03", "f04", "f05",
        "f06", "f07", "f08", "f09", "f10", "f11", "f12", "f13",
        "f14", "f15", "f16", "f17"
    });

    private static Dictionary<string, string?> MakeObject(int i) => new()
    {
        ["id"] = $"O{i + 1:D5}",
        ["date"] = $"2024-{(i % 12 + 1):D2}-{(i % 28 + 1):D2}",
        ["category"] = Categories[i % 4],
        ["f01"] = $"v{i % 100}",
        ["f02"] = i % 2 == 0 ? "A" : "B",
        ["f03"] = $"{(i % 1000) * 100}",
        ["f04"] = $"{99 + (i % 50):F2}",
        ["f05"] = "USD",
        ["f06"] = $"TRADER-{i % 10}",
        ["f07"] = $"CP-{i % 20}",
        ["f08"] = $"BOOK-{i % 5}",
        ["f09"] = $"PORT-{i % 8}",
        ["f10"] = "IRSwap",
        ["f11"] = $"2026-{(i % 12 + 1):D2}-01",
        ["f12"] = $"2024-{(i % 12 + 1):D2}-{((i % 28 + 2) % 28 + 1):D2}",
        ["f13"] = "LME",
        ["f14"] = $"STRAT-{i % 3}",
        ["f15"] = $"note-{i}",
        ["f16"] = $"ref-{i % 500}",
        ["f17"] = ""
    };

    private static Task<IngestResult> CreateObjects(ViewEngine engine) =>
        engine.IngestAsync(new CreateCollectionCommand { CollectionId = CollectionId, Schema = ObjectsSchema() });

    private static Task<IngestResult> UpsertObject(ViewEngine engine, int i) =>
        engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = $"O{i + 1:D5}",
            Fields = MakeObject(i)
        });

    private static async Task InsertObjectsAsync(ViewEngine engine, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var result = await UpsertObject(engine, i);
            Assert.True(result.Success, result.Error);
        }
    }

    // Collects all ids from a view by paging through the full result set.
    private static async Task<HashSet<string>> CollectAllIds(ViewEngine engine, string connectionId,
        ViewDefinition view, int total)
    {
        var allIds = new HashSet<string>(total);
        int pageSize = 500;
        for (int start = 0; start < total; start += pageSize)
        {
            var evts = await engine.SubscribeAsync(new SubscribeCommand
            {
                ConnectionId = $"{connectionId}-page-{start}",
                View = view,
                StartIndex = start,
                PageSize = pageSize
            });
            var snap = Assert.IsType<SnapshotDelta>(evts.Single());
            int idIdx = snap.Schema.GetFieldIndex("id");
            foreach (var row in snap.Rows)
            {
                allIds.Add(row[idIdx]!);
            }
        }

        return allIds;
    }

    [Fact]
    public async Task Insert10kObjects_Unfiltered_AllIdsReceived()
    {
        var (engine, _, _) = CreateEngine();
        await CreateObjects(engine);
        await InsertObjectsAsync(engine, 10000);

        var view = new ViewDefinition { CollectionId = CollectionId };
        var allIds = await CollectAllIds(engine, "client", view, 10000);

        Assert.Equal(10000, allIds.Count);
        for (int i = 1; i <= 10000; i++)
        {
            Assert.Contains($"O{i:D5}", allIds);
        }
    }

    [Fact]
    public async Task Insert10kObjects_SortedByDate_AllIdsAndOrderCorrect()
    {
        var (engine, _, _) = CreateEngine();
        await CreateObjects(engine);
        await InsertObjectsAsync(engine, 10000);

        var view = new ViewDefinition { CollectionId = CollectionId, SortColumn = "date", SortAscending = false };

        // Verify all ids present
        var allIds = await CollectAllIds(engine, "client", view, 10000);
        Assert.Equal(10000, allIds.Count);
        for (int i = 1; i <= 10000; i++)
        {
            Assert.Contains($"O{i:D5}", allIds);
        }

        // Verify page boundary ordering: last row of page N <= first row of page N+1 (desc)
        var evts0 = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "order-check-0",
            View = view,
            StartIndex = 0,
            PageSize = 500
        });
        var evts1 = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "order-check-1",
            View = view,
            StartIndex = 500,
            PageSize = 500
        });
        var snap0 = Assert.IsType<SnapshotDelta>(evts0.Single());
        var snap1 = Assert.IsType<SnapshotDelta>(evts1.Single());
        int dateIdx = snap0.Schema.GetFieldIndex("date");

        // Within first page: descending
        for (int i = 1; i < snap0.Rows.Count; i++)
        {
            Assert.True(string.Compare(snap0.Rows[i - 1][dateIdx], snap0.Rows[i][dateIdx], StringComparison.Ordinal) >=
                        0);
        }

        // Boundary: last of page 0 >= first of page 1
        Assert.True(string.Compare(snap0.Rows[^1][dateIdx], snap1.Rows[0][dateIdx], StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public async Task Insert10kObjects_FilteredByCategory_AllMatchingIdsReceived()
    {
        var (engine, _, _) = CreateEngine();
        await CreateObjects(engine);
        await InsertObjectsAsync(engine, 10000);

        var view = new ViewDefinition
        {
            CollectionId = CollectionId,
            Filters = [new FilterSpec("category", FilterOperator.Eq, "category1")]
        };

        // 10000 / 4 = 2500 category1 objects (i % 4 == 0 → i = 0,4,8,...,9996)
        var allIds = await CollectAllIds(engine, "client", view, 2500);
        Assert.Equal(2500, allIds.Count);

        // Every category1 id: O00001, O00005, O00009, ... (i%4==0 → i+1 = 1,5,9,...)
        for (int i = 0; i < 10000; i++)
        {
            if (i % 4 == 0)
            {
                Assert.Contains($"O{i + 1:D5}", allIds);
            }
        }
    }

    [Fact]
    public async Task Insert10kObjects_SortedAndFiltered_AllMatchingIdsAndOrderCorrect()
    {
        var (engine, _, _) = CreateEngine();
        await CreateObjects(engine);
        await InsertObjectsAsync(engine, 10000);

        var view = new ViewDefinition
        {
            CollectionId = CollectionId,
            SortColumn = "date",
            SortAscending = false,
            Filters = [new FilterSpec("category", FilterOperator.Eq, "category1")]
        };

        var allIds = await CollectAllIds(engine, "client", view, 2500);
        Assert.Equal(2500, allIds.Count);
        for (int i = 0; i < 10000; i++)
        {
            if (i % 4 == 0)
            {
                Assert.Contains($"O{i + 1:D5}", allIds);
            }
        }

        // Spot-check ordering on first page
        var evts = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "order-check",
            View = view,
            StartIndex = 0,
            PageSize = 500
        });
        var snap = Assert.IsType<SnapshotDelta>(evts.Single());
        int dateIdx = snap.Schema.GetFieldIndex("date");
        int catIdx = snap.Schema.GetFieldIndex("category");
        Assert.All(snap.Rows, row => Assert.Equal("category1", row[catIdx]));
        for (int i = 1; i < snap.Rows.Count; i++)
        {
            Assert.True(string.Compare(snap.Rows[i - 1][dateIdx], snap.Rows[i][dateIdx], StringComparison.Ordinal) >=
                        0);
        }
    }

    [Fact]
    public async Task Insert10kObjects_LiveStream_AllInsertEventsReceived()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateObjects(engine);

        // Sort descending so each new object enters at position 0, exercising the position-based delta path.
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client1",
            View = new ViewDefinition { CollectionId = CollectionId, SortColumn = "id", SortAscending = false },
            StartIndex = 0,
            PageSize = 50
        });

        await InsertObjectsAsync(engine, 10000);

        // Each insert goes to position 0 (descending sort, new id is always largest).
        // First 50: no remove. Subsequent 9950: 1 remove each.
        Assert.Equal(10000, publisher.EventsFor("client1").OfType<RowInsertEvent>().Count());
        Assert.Equal(9950, publisher.EventsFor("client1").OfType<RowRemoveEvent>().Count());
    }

    [Fact]
    public async Task Insert10kObjects_TwoSubscribers_BothReceiveAllIds()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateObjects(engine);

        var view = new ViewDefinition { CollectionId = CollectionId, SortColumn = "id", SortAscending = false };

        // Subscribe both clients before inserts
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "clientA",
            View = view,
            StartIndex = 0,
            PageSize = 50
        });
        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "clientB",
            View = view,
            StartIndex = 0,
            PageSize = 50
        });

        await InsertObjectsAsync(engine, 10000);

        // Both clients receive 10k inserts and 9950 removes (same logic as single-subscriber test)
        Assert.Equal(10000, publisher.EventsFor("clientA").OfType<RowInsertEvent>().Count());
        Assert.Equal(9950, publisher.EventsFor("clientA").OfType<RowRemoveEvent>().Count());
        Assert.Equal(10000, publisher.EventsFor("clientB").OfType<RowInsertEvent>().Count());
        Assert.Equal(9950, publisher.EventsFor("clientB").OfType<RowRemoveEvent>().Count());

        // After all inserts, verify both see the same final snapshot
        var evtsA = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "verifyA",
            View = view,
            StartIndex = 0,
            PageSize = 50
        });
        var evtsB = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "verifyB",
            View = view,
            StartIndex = 0,
            PageSize = 50
        });

        var snapA = Assert.IsType<SnapshotDelta>(evtsA.Single());
        var snapB = Assert.IsType<SnapshotDelta>(evtsB.Single());
        int idIdx = snapA.Schema.GetFieldIndex("id");

        Assert.Equal(10000, snapA.TotalCount);
        Assert.Equal(10000, snapB.TotalCount);

        // Both see identical first pages
        for (int i = 0; i < snapA.Rows.Count; i++)
        {
            Assert.Equal(snapA.Rows[i][idIdx], snapB.Rows[i][idIdx]);
        }
    }

    [Fact]
    public async Task Insert10kObjects_TwoSubscribers_DifferentViews_EachSeesOwnData()
    {
        var (engine, _, _) = CreateEngine();
        await CreateObjects(engine);
        await InsertObjectsAsync(engine, 10000);

        var viewCat1 = new ViewDefinition
        {
            CollectionId = CollectionId,
            Filters = [new FilterSpec("category", FilterOperator.Eq, "category1")]
        };
        var viewCat2 = new ViewDefinition
        {
            CollectionId = CollectionId,
            Filters = [new FilterSpec("category", FilterOperator.Eq, "category2")]
        };

        var idsCat1 = await CollectAllIds(engine, "cat1", viewCat1, 2500);
        var idsCat2 = await CollectAllIds(engine, "cat2", viewCat2, 2500);

        Assert.Equal(2500, idsCat1.Count);
        Assert.Equal(2500, idsCat2.Count);

        // Disjoint: no id belongs to both categories
        Assert.Empty(idsCat1.Intersect(idsCat2));

        // category1: i%4==0, category2: i%4==1
        for (int i = 0; i < 10000; i++)
        {
            if (i % 4 == 0)
            {
                Assert.Contains($"O{i + 1:D5}", idsCat1);
            }

            if (i % 4 == 1)
            {
                Assert.Contains($"O{i + 1:D5}", idsCat2);
            }
        }
    }

    private static readonly string[] ModifiableFields =
    [
        "f01", "f02", "f03", "f04", "f05", "f06", "f07", "f08", "f09", "f10", "f11", "f12", "f13", "f14", "f15", "f16",
        "f17"
    ];

    private static string[] PickFields(int count, Random rng)
    {
        var arr = (string[])ModifiableFields.Clone();
        for (int i = 0; i < count; i++)
        {
            int j = i + rng.Next(ModifiableFields.Length - i);
            (arr[i], arr[j]) = (arr[j], arr[i]);
        }

        return arr[..count];
    }

    private static (UpsertRowCommand Command, Dictionary<string, string?> Fields)[] GenerateModifications(int count,
        int seed = 42)
    {
        var rng = new Random(seed);
        var result = new (UpsertRowCommand, Dictionary<string, string?>)[count];
        for (int i = 0; i < count; i++)
        {
            int fieldCount = 1 + rng.Next(5);
            string[] chosen = PickFields(fieldCount, rng);
            var fields = new Dictionary<string, string?>(fieldCount);
            foreach (var f in chosen)
            {
                fields[f] = $"mod-{i}-{f}";
            }

            result[i] = (new UpsertRowCommand { CollectionId = CollectionId, Key = $"O{i + 1:D5}", Fields = fields },
                fields);
        }

        return result;
    }

    [Fact]
    public void ViewKey_IgnoresSelectedFields_WhenSortAndFiltersMatch()
    {
        var left = new ViewDefinition
        {
            CollectionId = CollectionId,
            SortColumn = "date",
            Filters = [new FilterSpec("category", FilterOperator.Eq, "category1")],
            Fields = ["id", "category", "f01"]
        };
        var right = new ViewDefinition
        {
            CollectionId = CollectionId,
            SortColumn = "date",
            Filters = [new FilterSpec("category", FilterOperator.Eq, "category1")],
            Fields = ["id", "f03", "f17"]
        };

        Assert.Equal(ViewKey.From(left), ViewKey.From(right));
        Assert.Equal(ViewKey.From(left).GetHashCode(), ViewKey.From(right).GetHashCode());
    }

    [Fact]
    public async Task Subscribe_WithSelectedFields_ProjectsOnlyRequestedColumns()
    {
        var (engine, _, _) = CreateEngine();
        await CreateObjects(engine);
        await UpsertObject(engine, 0);

        var events = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client",
            View = new ViewDefinition
            {
                CollectionId = CollectionId,
                Fields = ["id", "category", "f01"]
            },
            StartIndex = 0,
            PageSize = 50
        });

        var snapshot = Assert.IsType<SnapshotDelta>(events.Single());
        Assert.Collection(snapshot.Rows[0]!,
            value => Assert.Equal("O00001", value), // key auto-included at index 0
            value => Assert.Equal("O00001", value), // id at index 1
            value => Assert.Equal("category1", value),
            value => Assert.Equal("v0", value));
        Assert.Equal([0, 1, 3, 4], snapshot.VisibleFieldIndexes);
    }

    [Fact]
    public async Task Update_WithSelectedFields_OnlySelectedColumnsArePublished()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateObjects(engine);
        await UpsertObject(engine, 0);

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client",
            View = new ViewDefinition
            {
                CollectionId = CollectionId,
                Fields = ["id", "f01"]
            },
            StartIndex = 0,
            PageSize = 50
        });

        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = "O00001",
            Fields = new Dictionary<string, string?>
            {
                ["f01"] = "modified-value",
                ["f02"] = "B"
            }
        });

        var updates = publisher.EventsFor("client").OfType<RowUpdateEvent>().ToList();
        Assert.Single(updates);
        Assert.Equal("O00001", updates[0].RowId);
        Assert.Equal("modified-value", updates[0].ChangedFields["f01"]);
        Assert.DoesNotContain("f02", updates[0].ChangedFields.Keys);
    }

    [Fact]
    public async Task ModifyObject_SingleField_SubscriberReceivesOnlyThatField()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateObjects(engine);
        await UpsertObject(engine, 0);

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client",
            View = new ViewDefinition { CollectionId = CollectionId },
            StartIndex = 0,
            PageSize = 50
        });

        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = "O00001",
            Fields = new Dictionary<string, string?> { ["f01"] = "modified-value" }
        });

        var updates = publisher.EventsFor("client").OfType<RowUpdateEvent>().ToList();
        Assert.Single(updates);
        Assert.Equal("O00001", updates[0].RowId);
        Assert.Equal("modified-value", updates[0].ChangedFields["f01"]);
        Assert.Single(updates[0].ChangedFields);
    }

    [Fact]
    public async Task ModifyObject_MultipleFields_SubscriberReceivesExactlyThoseFields()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateObjects(engine);
        await UpsertObject(engine, 0);

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client",
            View = new ViewDefinition { CollectionId = CollectionId },
            StartIndex = 0,
            PageSize = 50
        });

        var changed = new Dictionary<string, string?> { ["f03"] = "x", ["f07"] = "y", ["f15"] = "z" };
        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = "O00001",
            Fields = changed
        });

        var updates = publisher.EventsFor("client").OfType<RowUpdateEvent>().ToList();
        Assert.Single(updates);
        Assert.Equal(3, updates[0].ChangedFields.Count);
        Assert.Equal("x", updates[0].ChangedFields["f03"]);
        Assert.Equal("y", updates[0].ChangedFields["f07"]);
        Assert.Equal("z", updates[0].ChangedFields["f15"]);
    }

    [Fact]
    public async Task Modify50Objects_RandomFields_SubscriberReceivesExactlyModifiedFields()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateObjects(engine);
        await InsertObjectsAsync(engine, 50);

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client",
            View = new ViewDefinition { CollectionId = CollectionId },
            StartIndex = 0,
            PageSize = 50
        });

        var mods = GenerateModifications(50);
        foreach (var (cmd, _) in mods)
        {
            await engine.IngestAsync(cmd);
        }

        var updates = publisher.EventsFor("client").OfType<RowUpdateEvent>().ToList();
        Assert.Equal(50, updates.Count);

        var expected = mods.ToDictionary(m => m.Command.Key, m => m.Fields);
        foreach (var update in updates)
        {
            Assert.True(expected.TryGetValue(update.RowId, out var expectedFields));
            Assert.Equal(expectedFields.Keys.Order(), update.ChangedFields.Keys.Order(), StringComparer.Ordinal);
            foreach (var (field, value) in expectedFields)
            {
                Assert.Equal(value, update.ChangedFields[field]);
            }
        }
    }

    [Fact]
    public async Task Modify100Objects_Sorted_OutOfViewportRowsProduceNoEvents()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateObjects(engine);
        await InsertObjectsAsync(engine, 100);

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client",
            View = new ViewDefinition { CollectionId = CollectionId, SortColumn = "id", SortAscending = true },
            StartIndex = 0,
            PageSize = 25
        });

        var mods = GenerateModifications(100);
        foreach (var (cmd, _) in mods)
        {
            await engine.IngestAsync(cmd);
        }

        var updates = publisher.EventsFor("client").OfType<RowUpdateEvent>().ToList();

        Assert.Equal(25, updates.Count);
        var inViewport = new HashSet<string>(Enumerable.Range(1, 25).Select(i => $"O{i:D5}"));
        Assert.All(updates, u => Assert.Contains(u.RowId, inViewport));
    }

    [Fact]
    public async Task LateSubscriber_AfterModifications_ReceivesSameStateAsEarlySubscriber()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateObjects(engine);
        await InsertObjectsAsync(engine, 100);

        var view = new ViewDefinition { CollectionId = CollectionId, SortColumn = "id", SortAscending = true };

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "earlyClient",
            View = view,
            StartIndex = 0,
            PageSize = 100
        });

        // Modify first 60 objects with seeded-random 1-5 fields each
        var mods = GenerateModifications(60);
        foreach (var (cmd, _) in mods)
        {
            await engine.IngestAsync(cmd);
        }

        // Early client should have received exactly 60 update events (all 60 are in viewport)
        var earlyUpdates = publisher.EventsFor("earlyClient").OfType<RowUpdateEvent>().ToList();
        Assert.Equal(60, earlyUpdates.Count);

        // Late subscriber joins after all modifications
        var lateDeltas = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "lateClient",
            View = view,
            StartIndex = 0,
            PageSize = 100
        });
        var lateSnap = Assert.IsType<SnapshotDelta>(lateDeltas.Single());

        // Subscribe a third connection to get a definitive snapshot for comparison
        var verifyDeltas = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "verify",
            View = view,
            StartIndex = 0,
            PageSize = 100
        });
        var verifySnap = Assert.IsType<SnapshotDelta>(verifyDeltas.Single());

        // Late subscriber and verify connection must see identical state
        Assert.Equal(verifySnap.TotalCount, lateSnap.TotalCount);
        Assert.Equal(verifySnap.Rows.Count, lateSnap.Rows.Count);
        int fieldCount = lateSnap.Schema.Fields.Count;
        for (int i = 0; i < lateSnap.Rows.Count; i++)
        {
            for (int f = 0; f < fieldCount; f++)
            {
                Assert.Equal(verifySnap.Rows[i][f], lateSnap.Rows[i][f]);
            }
        }

        // Verify the modifications are actually reflected: modified fields should have new values
        int idIdx = lateSnap.Schema.GetFieldIndex("id");
        var expectedMods = mods.ToDictionary(m => m.Command.Key, m => m.Fields);
        foreach (var row in lateSnap.Rows)
        {
            string rowId = row[idIdx]!;
            if (!expectedMods.TryGetValue(rowId, out var modFields))
            {
                continue;
            }

            foreach (var (fieldName, expectedValue) in modFields)
            {
                int fieldIdx = lateSnap.Schema.GetFieldIndex(fieldName);
                Assert.Equal(expectedValue, row[fieldIdx]);
            }
        }
    }

    [Fact]
    public async Task Subscribe_WithSelectedFields_KeyAlwaysIncludedEvenWhenNotRequested()
    {
        var (engine, _, _) = CreateEngine();
        await CreateObjects(engine);
        await UpsertObject(engine, 0);

        var events = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client",
            View = new ViewDefinition
            {
                CollectionId = CollectionId,
                Fields = ["f01", "f02"]
            },
            StartIndex = 0,
            PageSize = 50
        });

        var snapshot = Assert.IsType<SnapshotDelta>(events.Single());
        Assert.Contains(0, snapshot.VisibleFieldIndexes);
        Assert.Equal("O00001", snapshot.Rows[0]![0]);
    }

    [Fact]
    public async Task Subscribe_WithKeyExplicitlyRequested_KeyNotDuplicated()
    {
        var (engine, _, _) = CreateEngine();
        await CreateObjects(engine);
        await UpsertObject(engine, 0);

        var events = await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client",
            View = new ViewDefinition
            {
                CollectionId = CollectionId,
                Fields = ["key", "f01"]
            },
            StartIndex = 0,
            PageSize = 50
        });

        var snapshot = Assert.IsType<SnapshotDelta>(events.Single());
        Assert.Equal(2, snapshot.VisibleFieldIndexes!.Count);
        Assert.Equal(0, snapshot.VisibleFieldIndexes[0]);
    }

    [Fact]
    public async Task Update_OnlyUnselectedColumnsChanged_NoUpdatePublished()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateObjects(engine);
        await UpsertObject(engine, 0);

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client",
            View = new ViewDefinition
            {
                CollectionId = CollectionId,
                Fields = ["f01", "f02"]
            },
            StartIndex = 0,
            PageSize = 50
        });

        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = "O00001",
            Fields = new Dictionary<string, string?> { ["f05"] = "changed", ["f10"] = "changed" }
        });

        var updates = publisher.EventsFor("client").OfType<RowUpdateEvent>().ToList();
        Assert.Empty(updates);
    }

    [Fact]
    public async Task Update_MixOfSelectedAndUnselectedColumns_OnlySelectedPublished()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateObjects(engine);
        await UpsertObject(engine, 0);

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client",
            View = new ViewDefinition
            {
                CollectionId = CollectionId,
                Fields = ["f01", "f03"]
            },
            StartIndex = 0,
            PageSize = 50
        });

        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = "O00001",
            Fields = new Dictionary<string, string?>
            {
                ["f01"] = "selected-changed",
                ["f02"] = "unselected-changed",
                ["f03"] = "also-selected",
                ["f07"] = "also-unselected"
            }
        });

        var updates = publisher.EventsFor("client").OfType<RowUpdateEvent>().ToList();
        Assert.Single(updates);
        Assert.Equal(2, updates[0].ChangedFields.Count);
        Assert.Equal("selected-changed", updates[0].ChangedFields["f01"]);
        Assert.Equal("also-selected", updates[0].ChangedFields["f03"]);
    }

    [Fact]
    public async Task Delete_WithSelectedFields_RemoveEventAlwaysPublished()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateObjects(engine);
        await UpsertObject(engine, 0);

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client",
            View = new ViewDefinition
            {
                CollectionId = CollectionId,
                Fields = ["f01"]
            },
            StartIndex = 0,
            PageSize = 50
        });

        await engine.IngestAsync(new DeleteRowCommand
        {
            CollectionId = CollectionId,
            Key = "O00001"
        });

        var removes = publisher.EventsFor("client").OfType<RowRemoveEvent>().ToList();
        Assert.Single(removes);
    }

    [Fact]
    public async Task Insert_WithSelectedFields_InsertEventAlwaysPublished()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateObjects(engine);
        await UpsertObject(engine, 0);

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "client",
            View = new ViewDefinition
            {
                CollectionId = CollectionId,
                Fields = ["f01"]
            },
            StartIndex = 0,
            PageSize = 50
        });

        await UpsertObject(engine, 1);

        var inserts = publisher.EventsFor("client").OfType<RowInsertEvent>().ToList();
        Assert.Single(inserts);
        Assert.Contains("key", inserts[0].Row.Keys);
        Assert.Contains("f01", inserts[0].Row.Keys);
        Assert.DoesNotContain("f02", inserts[0].Row.Keys);
    }

    [Fact]
    public async Task TwoSubscribers_DifferentFieldSelections_EachReceivesOnlyTheirColumns()
    {
        var (engine, publisher, _) = CreateEngine();
        await CreateObjects(engine);
        await UpsertObject(engine, 0);

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "clientA",
            View = new ViewDefinition { CollectionId = CollectionId, Fields = ["f01", "f02"] },
            StartIndex = 0,
            PageSize = 50
        });

        await engine.SubscribeAsync(new SubscribeCommand
        {
            ConnectionId = "clientB",
            View = new ViewDefinition { CollectionId = CollectionId, Fields = ["f03", "f04"] },
            StartIndex = 0,
            PageSize = 50
        });

        await engine.IngestAsync(new UpsertRowCommand
        {
            CollectionId = CollectionId,
            Key = "O00001",
            Fields = new Dictionary<string, string?>
            {
                ["f01"] = "a1", ["f02"] = "a2",
                ["f03"] = "b3", ["f04"] = "b4"
            }
        });

        var updatesA = publisher.EventsFor("clientA").OfType<RowUpdateEvent>().ToList();
        var updatesB = publisher.EventsFor("clientB").OfType<RowUpdateEvent>().ToList();

        Assert.Single(updatesA);
        Assert.Contains("f01", updatesA[0].ChangedFields.Keys);
        Assert.Contains("f02", updatesA[0].ChangedFields.Keys);
        Assert.DoesNotContain("f03", updatesA[0].ChangedFields.Keys);

        Assert.Single(updatesB);
        Assert.Contains("f03", updatesB[0].ChangedFields.Keys);
        Assert.Contains("f04", updatesB[0].ChangedFields.Keys);
        Assert.DoesNotContain("f01", updatesB[0].ChangedFields.Keys);
    }
}