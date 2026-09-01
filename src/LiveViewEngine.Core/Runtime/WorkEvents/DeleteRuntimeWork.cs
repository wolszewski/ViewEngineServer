using LiveViewEngine.Core.DataIngest;

namespace LiveViewEngine.Core.Runtime.WorkEvents;

internal sealed class DeleteRuntimeWork : RuntimeWorkItem<MutationResult>
{
    private readonly CollectionRuntime _runtime;
    private readonly DeleteRowCommand _command;

    public DeleteRuntimeWork(
        CollectionRuntime runtime,
        DeleteRowCommand command,
        Action<MutationResult>? onCompleted = null) : base(onCompleted)
    {
        _runtime = runtime;
        _command = command;
    }

    protected override MutationResult ExecuteCore() => _runtime.HandleDelete(_command);
}