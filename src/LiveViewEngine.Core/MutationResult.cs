namespace LiveViewEngine.Core;

public readonly record struct MutationResult
{
    public MutationResult(IngestResult Result,
        List<(IReadOnlyList<ViewDelta> Deltas, List<string> ConnectionIds)>? Groups)
    {
        this.Result = Result;
        this.Groups = Groups;
    }

    public IngestResult Result { get; init; }
    public List<(IReadOnlyList<ViewDelta> Deltas, List<string> ConnectionIds)>? Groups { get; init; }
}