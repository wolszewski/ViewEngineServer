namespace LiveViewEngine.Core.Runtime.WorkEvents;

internal sealed class UnsubscribeRuntimeWork(
    CollectionRuntime runtime,
    UnsubscribeCommand command
) : RuntimeWorkItem<IReadOnlyList<ViewDelta>>
{
    protected override IReadOnlyList<ViewDelta> ExecuteCore() => runtime.HandleUnsubscribe(command);
}