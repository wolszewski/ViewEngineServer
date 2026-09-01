namespace LiveViewEngine.Core.Runtime.WorkEvents;

internal sealed class UpdateViewRuntimeWork(
    CollectionRuntime runtime,
    UpdateViewCommand command,
    Action? onBeforeExecute = null
)
    : RuntimeWorkItem<IReadOnlyList<ViewDelta>>
{
    protected override IReadOnlyList<ViewDelta> ExecuteCore()
    {
        onBeforeExecute?.Invoke();
        return runtime.HandleUpdateView(command);
    }
}