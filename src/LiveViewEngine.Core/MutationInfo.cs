namespace LiveViewEngine.Core;

public sealed record MutationInfo(
    string RowId,
    int Index,
    bool IsNew,
    string?[]? PreviousValues,
    string?[]? NewValues);