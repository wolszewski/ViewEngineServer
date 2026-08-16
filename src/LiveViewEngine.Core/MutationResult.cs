namespace LiveViewEngine.Core;

public readonly record struct MutationResult(
    IngestResult Result,
    List<(IReadOnlyList<ViewDelta> Deltas,
        List<string> ConnectionIds)>? Groups);