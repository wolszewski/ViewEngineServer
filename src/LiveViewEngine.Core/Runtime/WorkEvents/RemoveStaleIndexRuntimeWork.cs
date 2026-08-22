namespace LiveViewEngine.Core.Runtime.WorkEvents;

internal sealed class RemoveStaleIndexRuntimeWork : RuntimeWorkItem<bool>
{
    private readonly CollectionRuntime _runtime;
    private readonly SortIndexKey _key;

    public RemoveStaleIndexRuntimeWork(CollectionRuntime runtime, SortIndexKey key)
    {
        _runtime = runtime;
        _key = key;
    }

    protected override bool ExecuteCore() => _runtime.RemoveStaleIndex(_key);
}