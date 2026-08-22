using LiveViewEngine.Core.DataIngest;

namespace LiveViewEngine.Core.Runtime.WorkEvents;

internal sealed class RegisterFilterPresetRuntimeWork : RuntimeWorkItem<IngestResult>
{
    private readonly CollectionRuntime _runtime;
    private readonly string _filterPresetId;
    private readonly IReadOnlyList<FilterSpec> _filters;

    public RegisterFilterPresetRuntimeWork(
        CollectionRuntime runtime,
        string filterPresetId,
        IReadOnlyList<FilterSpec> filters)
    {
        _runtime = runtime;
        _filterPresetId = filterPresetId;
        _filters = filters;
    }

    protected override IngestResult ExecuteCore() => _runtime.RegisterFilterPreset(_filterPresetId, _filters);
}
