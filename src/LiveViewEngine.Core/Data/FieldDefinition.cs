namespace LiveViewEngine.Core.Data;

public sealed record FieldDefinition(
    string Name,
    int FieldIndex,
    ScalarFieldType Type = ScalarFieldType.String,
    int TypedColumnIndex = -1);