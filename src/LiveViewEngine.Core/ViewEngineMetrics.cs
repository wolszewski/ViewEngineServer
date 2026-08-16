using System.Diagnostics.Metrics;

namespace LiveViewEngine.Core;

public interface IViewEngineMetrics
{
    Histogram<double> InsertDuration { get; }
    Histogram<double> UpdateDuration { get; }
    Histogram<double> SubscriptionDuration { get; }
    Counter<long> InsertCount { get; }
    Counter<long> UpdateCount { get; }

    void RegisterGaugeSources(
        Func<int> activeSubscriptions,
        Func<int> activeSharedViews,
        Func<int> activeSortIndexes);
}

public sealed class ViewEngineMetrics : IDisposable, IViewEngineMetrics
{
    private readonly Meter _meter = new("ViewEngineServer");

    public ViewEngineMetrics()
    {
        InsertDuration = _meter.CreateHistogram<double>(
            "viewengine.insert.duration",
            unit: "ms",
            description: "Time spent processing insert operations.");

        UpdateDuration = _meter.CreateHistogram<double>(
            "viewengine.update.duration",
            unit: "ms",
            description: "Time spent processing update operations.");

        SubscriptionDuration = _meter.CreateHistogram<double>(
            "viewengine.subscription.duration",
            unit: "ms",
            description: "Time spent processing subscription operations.");

        InsertCount = _meter.CreateCounter<long>(
            "viewengine.insert.count",
            description: "Total number of insert operations processed.");

        UpdateCount = _meter.CreateCounter<long>(
            "viewengine.update.count",
            description: "Total number of update operations processed.");
    }

    public Histogram<double> InsertDuration { get; }
    public Histogram<double> UpdateDuration { get; }
    public Histogram<double> SubscriptionDuration { get; }
    public Counter<long> InsertCount { get; }
    public Counter<long> UpdateCount { get; }

    public void RegisterGaugeSources(
        Func<int> activeSubscriptions,
        Func<int> activeSharedViews,
        Func<int> activeSortIndexes)
    {
        _meter.CreateObservableGauge(
            "viewengine.active_subscriptions",
            activeSubscriptions,
            description: "Number of active viewport subscriptions across all collections.");

        _meter.CreateObservableGauge(
            "viewengine.active_shared_views",
            activeSharedViews,
            description: "Number of active shared views (sort+filter combinations) across all collections.");

        _meter.CreateObservableGauge(
            "viewengine.active_sort_indexes",
            activeSortIndexes,
            description: "Number of active sort indexes across all collections.");
    }

    public void Dispose() => _meter.Dispose();
}