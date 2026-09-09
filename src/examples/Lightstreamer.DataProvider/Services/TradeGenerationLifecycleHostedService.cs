using LiveViewEngine.Poc.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lightstreamer.DataProvider.Services;

public sealed class TradeGenerationLifecycleHostedService(
    TradeCommandProvider commandProvider,
    TradePureCommandDataProvider pureCommandProvider,
    TradeMergeDataProvider mergeDataProvider,
    TradeGeneratorService tradeGenerator,
    TradeGenerationSettingsStore settingsStore,
    ILogger<TradeGenerationLifecycleHostedService> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _sync = new();
    private Task _transitionTask = Task.CompletedTask;
    private bool _started;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        commandProvider.ListSubscribed += HandleCommandListSubscribed;
        commandProvider.ListUnsubscribed += HandleListUnsubscribed;
        pureCommandProvider.ListSubscribed += HandlePureListSubscribed;
        pureCommandProvider.ListUnsubscribed += HandleListUnsubscribed;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        commandProvider.ListSubscribed -= HandleCommandListSubscribed;
        commandProvider.ListUnsubscribed -= HandleListUnsubscribed;
        pureCommandProvider.ListSubscribed -= HandlePureListSubscribed;
        pureCommandProvider.ListUnsubscribed -= HandleListUnsubscribed;
        _cts.Cancel();
        tradeGenerator.StopGeneration();
        await _transitionTask.ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private void HandleCommandListSubscribed() => HandleListSubscribed(commandProvider.PublishSnapshotAndEnableLiveUpdates);

    private void HandlePureListSubscribed() => HandleListSubscribed(pureCommandProvider.PublishSnapshotAndEnableLiveUpdates);

    private void HandleListSubscribed(Action publishSnapshotForSubscriber)
    {
        lock (_sync)
        {
            if (_started)
            {
                // The other mode's list item is already driving generation - the data already
                // exists, so just publish the current snapshot to the newly subscribed item
                // instead of restarting the whole generator (which would also wipe the data the
                // other mode is actively streaming).
                logger.LogInformation("Generation already running; publishing existing snapshot to newly subscribed item.");
                QueuePublishOnly(publishSnapshotForSubscriber);
                return;
            }

            _started = true;
            QueueTransition(startRequested: true);
        }
    }

    private void HandleListUnsubscribed()
    {
        lock (_sync)
        {
            if (!_started)
            {
                return;
            }

            // Two independently switchable modes share one generator - only stop once neither
            // mode's list item is subscribed anymore.
            if (commandProvider.IsSubscribed || pureCommandProvider.IsSubscribed)
            {
                return;
            }

            _started = false;
            QueueTransition(startRequested: false);
        }
    }

    private void QueueTransition(bool startRequested)
    {
        _transitionTask = _transitionTask.ContinueWith(
            _ => RunTransitionAsync(startRequested),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();
    }

    // Chained onto the same transition queue as start/stop transitions so it can't run
    // concurrently with (and see partially reset state from) a stop/start transition.
    private void QueuePublishOnly(Action publishSnapshotForSubscriber)
    {
        _transitionTask = _transitionTask.ContinueWith(
            _ =>
            {
                if (!_cts.IsCancellationRequested && tradeGenerator.IsRunning)
                {
                    publishSnapshotForSubscriber();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private async Task RunTransitionAsync(bool startRequested)
    {
        try
        {
            tradeGenerator.StopGeneration();
            await WaitUntilStoppedAsync(_cts.Token).ConfigureAwait(false);

            mergeDataProvider.ResetData();
            commandProvider.ResetKeys();
            pureCommandProvider.ResetData();

            if (!startRequested || _cts.IsCancellationRequested)
            {
                logger.LogInformation("Trade generation stopped after command adapter unsubscribe.");
                return;
            }

            var settings = settingsStore.GetSnapshot();
            logger.LogInformation(
                "Starting trade generation after command adapter subscribe. InitialTradeCount={InitialTradeCount}, UpdateFieldCount={UpdateFieldCount}, UpdateFrequencyHz={UpdateFrequencyHz}",
                settings.InitialTradeCount,
                settings.UpdateFieldCount,
                settings.UpdateFrequencyHz);

            _ = tradeGenerator.StartGenerationAsync(settings, _cts.Token);
            await WaitUntilInitialLoadCompleteAsync(settings.InitialTradeCount, _cts.Token).ConfigureAwait(false);
            if (!_cts.IsCancellationRequested)
            {
                commandProvider.PublishSnapshotAndEnableLiveUpdates();
                pureCommandProvider.PublishSnapshotAndEnableLiveUpdates();
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
    }

    private async Task WaitUntilStoppedAsync(CancellationToken ct)
    {
        while (tradeGenerator.IsRunning && !ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    private async Task WaitUntilInitialLoadCompleteAsync(int expectedInitialCount, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var status = tradeGenerator.Status;
            if (status.IsInUpdateMode && status.TradesGenerated >= expectedInitialCount)
            {
                return;
            }

            if (!tradeGenerator.IsRunning)
            {
                return;
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }
}
