namespace LiveViewEngine.Core.Runtime;

internal sealed class UpdateViewRuntimeWork : RuntimeWorkItem<IReadOnlyList<ViewDelta>>
{
    private readonly CollectionRuntime _runtime;
    private readonly UpdateViewCommand _command;

    public UpdateViewRuntimeWork(CollectionRuntime runtime, UpdateViewCommand command)
    {
        _runtime = runtime;
        _command = command;
    }

    protected override IReadOnlyList<ViewDelta> ExecuteCore() => _runtime.HandleUpdateView(_command);
}
