namespace LiveViewEngine.Core.Runtime;

internal sealed class RemoveStaleTypedColumnRuntimeWork : RuntimeWorkItem<bool>
{
    private readonly CollectionRuntime _runtime;
    private readonly int _fieldIndex;

    public RemoveStaleTypedColumnRuntimeWork(CollectionRuntime runtime, int fieldIndex)
    {
        _runtime = runtime;
        _fieldIndex = fieldIndex;
    }

    protected override bool ExecuteCore() => _runtime.RemoveStaleTypedColumn(_fieldIndex);
}
