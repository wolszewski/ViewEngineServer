namespace LiveViewEngine.Core;

public sealed record MutationInfo(
    string RowId,
    int RowIndex,
    bool IsNew,
    IReadOnlyCollection<KeyValuePair<int, string?>>? ChangedColumns,
    FieldMask ChangedMask);