using LiveViewEngine.Core.DataIngest;

namespace LiveViewEngine.Core.Runtime.WorkEvents;

internal sealed class UpsertRuntimeWork(
    CollectionRuntime runtime,
    UpsertRowCommand command,
    Func<MutationResult, ValueTask>? onCompleted = null)
    : RuntimeWorkItem<MutationResult>(onCompleted)
{
    protected override MutationResult ExecuteCore() => runtime.HandleUpsert(command);
}