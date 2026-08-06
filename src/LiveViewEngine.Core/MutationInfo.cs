namespace LiveViewEngine.Core;

public sealed record MutationInfo(
    string RowId,
    int Index,
    bool IsNew,
    IReadOnlyCollection<KeyValuePair<int, string?>>? ChangedColumns);