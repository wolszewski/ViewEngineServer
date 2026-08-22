namespace LiveViewEngine.Core.Runtime;

internal sealed class UnsubscribeRuntimeWork : RuntimeWorkItem<IReadOnlyList<ViewDelta>>
{
    private readonly CollectionRuntime _runtime;
    private readonly UnsubscribeCommand _command;

    public UnsubscribeRuntimeWork(CollectionRuntime runtime, UnsubscribeCommand command)
    {
        _runtime = runtime;
        _command = command;
    }

    protected override IReadOnlyList<ViewDelta> ExecuteCore() => _runtime.HandleUnsubscribe(_command);
}