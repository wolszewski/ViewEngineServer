namespace LiveViewEngine.Core.Runtime;

internal sealed class RegisterSegmentRuntimeWork : RuntimeWorkItem<IngestResult>
{
    private readonly CollectionRuntime _runtime;
    private readonly string _segmentId;
    private readonly IReadOnlyList<FilterSpec> _filters;

    public RegisterSegmentRuntimeWork(CollectionRuntime runtime, string segmentId, IReadOnlyList<FilterSpec> filters)
    {
        _runtime = runtime;
        _segmentId = segmentId;
        _filters = filters;
    }

    protected override IngestResult ExecuteCore() => _runtime.RegisterSegment(_segmentId, _filters);
}
