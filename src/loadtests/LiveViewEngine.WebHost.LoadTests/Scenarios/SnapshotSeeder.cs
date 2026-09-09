using LiveViewEngine.WebHost.LoadTests.Config;
using LiveViewEngine.WebHost.LoadTests.Ingest;
using Microsoft.Extensions.Logging;

namespace LiveViewEngine.WebHost.LoadTests.Scenarios;

/// <summary>
/// One-time, up-front seeding of subscribe_latency's collection so the measured snapshot load
/// reflects a realistic row count instead of an empty collection.
/// </summary>
public static class SnapshotSeeder
{
    public static async Task SeedAsync(
        LoadTestSettings settings,
        IIngestClient ingestClient,
        ILogger logger,
        CancellationToken ct = default)
    {
        using var throttle = new SemaphoreSlim(settings.SubscribeSeedConcurrency);
        var tasks = new List<Task>(settings.SubscribeSeedRowCount);

        for (var i = 0; i < settings.SubscribeSeedRowCount; i++)
        {
            var index = i;
            await throttle.WaitAsync(ct);
            tasks.Add(SeedOneAsync(settings, ingestClient, throttle, index, ct));
        }

        await Task.WhenAll(tasks);

        logger.LogInformation(
            "subscribe_latency: seeded {Count} rows into '{Collection}'.",
            settings.SubscribeSeedRowCount,
            settings.CollectionName);
    }

    private static async Task SeedOneAsync(
        LoadTestSettings settings,
        IIngestClient ingestClient,
        SemaphoreSlim throttle,
        int index,
        CancellationToken ct)
    {
        try
        {
            var rowKey = $"seed-{index}";
            await ingestClient.IngestAsync(
                settings.CollectionName,
                rowKey,
                new Dictionary<string, string?>
                {
                    ["symbol"] = rowKey,
                    ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O")
                },
                ct);
        }
        finally
        {
            throttle.Release();
        }
    }
}
