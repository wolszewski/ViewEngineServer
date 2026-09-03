using LiveViewEngine.Core.Views;

namespace LiveViewEngine.Core.Runtime;

// Owns everything specific to server-side sorting: whether it's enabled and what counts as a
// sort request. CollectionRuntime only calls through this interface - it has no built-in
// knowledge of what "sorting" means beyond "ask the capability". The actual ordering mechanism
// (NaturalOrderIndex vs. SortIndex) is a separate concern selected via IPositionIndex.
public interface ISortingCapability
{
    (string Reason, string Message)? Validate(ViewDefinition view);
}

public sealed class SortingCapability(bool enabled) : ISortingCapability
{
    public (string Reason, string Message)? Validate(ViewDefinition view)
    {
        if (view.SortColumn is not null && !enabled)
        {
            return ("sorting_not_enabled", "Server-side sorting is not enabled for this deployment.");
        }

        return null;
    }
}
