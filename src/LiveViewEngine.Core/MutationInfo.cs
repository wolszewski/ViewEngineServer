namespace LiveViewEngine.Core;

// ChangedColumns is the concrete array rather than IReadOnlyCollection so propagation can pass it
// as a span: it is walked once per subscriber, and enumerating through the interface would box an
// enumerator on each of those walks.
public sealed record MutationInfo(
    string RowId,
    int RowIndex,
    bool IsNew,
    KeyValuePair<int, string?>[]? ChangedColumns);
