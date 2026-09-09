namespace LiveViewEngine.WebHost.LoadTests.Config;

public enum IngestMode
{
    Http,
    Tcp
}

public enum LoadTestScenario
{
    All,
    Subscribe,
    Delta
}

/// <summary>
/// Load test settings bound from appsettings.json ("LoadTest" section) and overridable by a
/// handful of dedicated CLI flags parsed in Program.cs before NBomber's own CLI args are
/// forwarded to NBomberRunner.Run(...).
/// </summary>
public sealed class LoadTestSettings
{
    public string WebSocketUrl { get; set; } = "ws://localhost:5038/ws";
    public string BaseHttpUrl { get; set; } = "http://localhost:5038";
    public string TcpHost { get; set; } = "127.0.0.1";
    public int TcpPort { get; set; } = 6000;

    public string CollectionName { get; set; } = "loadtest-trades";

    public IngestMode IngestMode { get; set; } = IngestMode.Http;
    public LoadTestScenario Scenario { get; set; } = LoadTestScenario.All;

    /// <summary>Enables the TimescaleDbSink reporting sink for NBomber Studio (nbomber/docker-compose.yaml).</summary>
    public bool EnableStudio { get; set; }

    public string TimescaleConnectionString { get; set; } =
        "Host=localhost;Port=5432;Database=nb_studio_db;Username=nb_studio_db;Password=nb_studio_db;Pooling=true";

    public int SubscribeSnapshotPageSize { get; set; } = 500;
    public int SubscribeCopies { get; set; } = 50;
    public int SubscribeDurationSeconds { get; set; } = 30;

    /// <summary>Rows ingested into CollectionName once, up-front, so subscribe_latency measures loading a
    /// realistic snapshot rather than an empty one.</summary>
    public int SubscribeSeedRowCount { get; set; } = 10_000;
    public int SubscribeSeedConcurrency { get; set; } = 50;

    /// <summary>Collection updated periodically in the background and observed by DeltaSubscriberCount
    /// persistent WS subscribers - kept separate from CollectionName so it isn't polluted by
    /// subscribe_latency's seeded snapshot rows.</summary>
    public string DeltaCollectionName { get; set; } = "loadtest-delta";

    /// <summary>The row's creation timestamp field; set once on insert and never changed afterwards.</summary>
    public string DeltaCreatedAtField { get; set; } = "createdAt";

    /// <summary>Rewritten with the current UTC timestamp on every insert/update tick; observed latency
    /// = receivedAt - parse(this field's value).</summary>
    public string DeltaUpdatedAtField { get; set; } = "updatedAt";

    /// <summary>Rate (updates/sec) at which the background updater writes DeltaUpdatedAtField on the
    /// single row in DeltaCollectionName, independent of DeltaSubscriberCount.</summary>
    public double DeltaUpdatesPerSecond { get; set; } = 20;

    /// <summary>Number of persistent WS subscribers observing the broadcast updates; also the number of
    /// NBomber virtual users (1 subscriber per copy, opened lazily on that copy's first iteration).</summary>
    public int DeltaSubscriberCount { get; set; } = 50;

    public int DeltaDurationSeconds { get; set; } = 30;
    public int DeltaAwaitTimeoutSeconds { get; set; } = 10;
}
