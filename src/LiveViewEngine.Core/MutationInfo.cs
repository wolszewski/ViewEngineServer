namespace LiveViewEngine.Core;

public sealed record MutationInfo(
    string RowId,
    int Handle,
    bool IsNew,
    string?[]? PreviousValues,
    string?[]? NewValues);