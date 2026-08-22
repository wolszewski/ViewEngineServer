using LiveViewEngine.Core.DataIngest;

namespace LiveViewEngine.Core.Runtime.WorkEvents;

internal sealed class UpsertRuntimeWork(
    CollectionRuntime runtime, 
    UpsertRowCommand command)
    : RuntimeWorkItem<MutationResult>
{
    protected override MutationResult ExecuteCore() => runtime.HandleUpsert(command);
}