namespace ViewEngineServer.WebApp.Core;

public sealed record MutationInfo(
    string RowId,
    int Handle,
    bool IsNew,
    string?[]? PreviousValues,
    string?[]? NewValues);