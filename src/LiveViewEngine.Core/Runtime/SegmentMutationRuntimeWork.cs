namespace LiveViewEngine.Core.Runtime;

internal sealed class SegmentMutationRuntimeWork : RuntimeWorkItem<List<(IReadOnlyList<ViewDelta>, List<SubscriberTarget>)>?>
{
    private readonly SegmentRuntime _segmentRuntime;
    private readonly CollectionRuntime _collectionRuntime;
    private readonly MutationInfo _mutation;
    private readonly bool _isDelete;

    public SegmentMutationRuntimeWork(
        SegmentRuntime segmentRuntime,
        CollectionRuntime collectionRuntime,
        MutationInfo mutation,
        bool isDelete)
    {
        _segmentRuntime = segmentRuntime;
        _collectionRuntime = collectionRuntime;
        _mutation = mutation;
        _isDelete = isDelete;
    }

    protected override List<(IReadOnlyList<ViewDelta>, List<SubscriberTarget>)>? ExecuteCore()
    {
        return _segmentRuntime.Propagate(
            _collectionRuntime.Collection,
            _collectionRuntime.Viewports,
            _mutation,
            _isDelete);
    }
}
