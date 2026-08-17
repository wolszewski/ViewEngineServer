namespace LiveViewEngine.Core;

public readonly record struct MutationResult
{
    public MutationResult(IngestResult Result,
        List<(IReadOnlyList<ViewDelta> Deltas, List<SubscriberTarget> Targets)>? Groups)
    {
        this.Result = Result;
        this.Groups = Groups;
    }

    public IngestResult Result { get; init; }
    public List<(IReadOnlyList<ViewDelta> Deltas, List<SubscriberTarget> Targets)>? Groups { get; init; }
}