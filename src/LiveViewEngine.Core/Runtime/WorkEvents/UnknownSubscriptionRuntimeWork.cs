namespace LiveViewEngine.Core.Runtime;

internal sealed class UnknownSubscriptionRuntimeWork : RuntimeWorkItem<IReadOnlyList<ViewDelta>>
{
    private readonly SubscriptionCommand _command;

    public UnknownSubscriptionRuntimeWork(SubscriptionCommand command)
    {
        _command = command;
    }

    protected override IReadOnlyList<ViewDelta> ExecuteCore() => [];
}