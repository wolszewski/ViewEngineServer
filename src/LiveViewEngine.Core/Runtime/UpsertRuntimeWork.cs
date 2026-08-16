namespace LiveViewEngine.Core.Runtime;

internal sealed class UpsertRuntimeWork : RuntimeWorkItem<MutationResult>
{
    private readonly CollectionRuntime _runtime;
    private readonly UpsertRowCommand _command;

    public UpsertRuntimeWork(CollectionRuntime runtime, UpsertRowCommand command)
    {
        _runtime = runtime;
        _command = command;
    }

    protected override MutationResult ExecuteCore() => _runtime.HandleUpsert(_command);
}