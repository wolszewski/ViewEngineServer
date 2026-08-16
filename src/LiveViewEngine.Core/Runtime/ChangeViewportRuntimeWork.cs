namespace LiveViewEngine.Core.Runtime;

internal sealed class ChangeViewportRuntimeWork : RuntimeWorkItem<IReadOnlyList<ViewDelta>>
{
    private readonly CollectionRuntime _runtime;
    private readonly ChangeViewportCommand _command;

    public ChangeViewportRuntimeWork(CollectionRuntime runtime, ChangeViewportCommand command)
    {
        _runtime = runtime;
        _command = command;
    }

    protected override IReadOnlyList<ViewDelta> ExecuteCore() => _runtime.HandleChangeViewport(_command);
}