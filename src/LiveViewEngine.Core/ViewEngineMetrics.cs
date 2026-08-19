using System.Diagnostics.Metrics;

namespace LiveViewEngine.Core;

public interface IViewEngineMetrics
{
    void RegisterGaugeSources(
        Func<int> activeSubscriptions,
        Func<int> activeSharedViews,
        Func<int> activeSortIndexes);

    void RegisterTypedColumnGaugeSource(Func<IEnumerable<Measurement<int>>> source);
    void RegisterCollectionQueueDepthGaugeSource(Func<IEnumerable<Measurement<int>>> source);

    void RecordInsert(double durationMs, string collectionId);
    void RecordUpdate(double durationMs, string collectionId);
    void RecordSubscriptionDuration(double durationMs, string commandType, string collectionId);
}

public sealed class ViewEngineMetrics : IDisposable, IViewEngineMetrics
{
    private readonly Meter _meter = new("ViewEngineServer");
    private readonly Histogram<double> _insertDuration;
    private readonly Histogram<double> _updateDuration;
    private readonly Histogram<double> _subscriptionDuration;
    private readonly Counter<long> _insertCount;
    private readonly Counter<long> _updateCount;

    public ViewEngineMetrics()
    {
        _insertDuration = _meter.CreateHistogram<double>(
            "viewengine.insert.duration",
            unit: "ms",
            description: "Time spent processing insert operations.");

        _updateDuration = _meter.CreateHistogram<double>(
            "viewengine.update.duration",
            unit: "ms",
            description: "Time spent processing update operations.");

        _subscriptionDuration = _meter.CreateHistogram<double>(
            "viewengine.subscription.duration",
            unit: "ms",
            description: "Time spent processing subscription operations.");

        _insertCount = _meter.CreateCounter<long>(
            "viewengine.insert.count",
            description: "Total number of insert operations processed.");

        _updateCount = _meter.CreateCounter<long>(
            "viewengine.update.count",
            description: "Total number of update operations processed.");
    }

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

    public void RegisterTypedColumnGaugeSource(Func<IEnumerable<Measurement<int>>> source)
    {
        _meter.CreateObservableGauge(
            "viewengine.typed_columns.ref_count",
            source,
            description: "Ref count per active typed column, tagged by collectionId and fieldName.");
    }

    public void RegisterCollectionQueueDepthGaugeSource(Func<IEnumerable<Measurement<int>>> source)
    {
        _meter.CreateObservableGauge(
            "viewengine.collection.channel_depth",
            source,
            description: "Current number of queued work items in each collection worker channel, tagged by collectionId.");
    }

    public void RecordInsert(double durationMs, string collectionId)
    {
        _insertDuration.Record(durationMs, new KeyValuePair<string, object?>("collectionId", collectionId));
        _insertCount.Add(1, new KeyValuePair<string, object?>("collectionId", collectionId));
    }

    public void RecordUpdate(double durationMs, string collectionId)
    {
        _updateDuration.Record(durationMs, new KeyValuePair<string, object?>("collectionId", collectionId));
        _updateCount.Add(1, new KeyValuePair<string, object?>("collectionId", collectionId));
    }

    public void RecordSubscriptionDuration(double durationMs, string commandType, string collectionId)
    {
        _subscriptionDuration.Record(
            durationMs,
            new KeyValuePair<string, object?>("commandType", commandType),
            new KeyValuePair<string, object?>("collectionId", collectionId));
    }

    public void Dispose() => _meter.Dispose();
}