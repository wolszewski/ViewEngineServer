using LiveViewEngine.Core.Data;
using Microsoft.Extensions.Hosting;

namespace LiveViewEngine.Core;

internal sealed class StaleIndexReaperService(ICollectionStore store, LiveViewEngineOptions options) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.EagerIndexing)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            foreach (var collectionId in store.CollectionIds)
            {
                if (store.TryGetRuntime(collectionId, out var runtime) && runtime is not null)
                {
                    await runtime.ReapOnceAsync(stoppingToken).ConfigureAwait(false);
                }
            }
        }
    }
}
