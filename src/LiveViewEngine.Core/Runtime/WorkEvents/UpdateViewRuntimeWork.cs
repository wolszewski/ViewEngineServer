namespace LiveViewEngine.Core.Runtime.WorkEvents;

internal sealed class UpdateViewRuntimeWork(
    CollectionRuntime runtime,
    UpdateViewCommand command
)
    : RuntimeWorkItem<IReadOnlyList<ViewDelta>>
{
    protected override IReadOnlyList<ViewDelta> ExecuteCore() => runtime.HandleUpdateView(command);
}