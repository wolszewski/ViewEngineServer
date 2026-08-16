namespace LiveViewEngine.Core.Runtime;

internal sealed class SubscribeRuntimeWork : RuntimeWorkItem<IReadOnlyList<ViewDelta>>
{
    private readonly CollectionRuntime _runtime;
    private readonly SubscribeCommand _command;

    public SubscribeRuntimeWork(CollectionRuntime runtime, SubscribeCommand command)
    {
        _runtime = runtime;
        _command = command;
    }

    protected override IReadOnlyList<ViewDelta> ExecuteCore() => _runtime.HandleSubscribe(_command);
}