namespace LiveViewEngine.Core.Runtime.WorkEvents;

internal sealed class UnknownSubscriptionRuntimeWork(SubscriptionCommand command)
    : RuntimeWorkItem<IReadOnlyList<ViewDelta>>
{
    private readonly SubscriptionCommand _command = command;

    protected override IReadOnlyList<ViewDelta> ExecuteCore() => [];
}