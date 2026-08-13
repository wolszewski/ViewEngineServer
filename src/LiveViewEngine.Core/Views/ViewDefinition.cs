namespace LiveViewEngine.Core.Views;

public sealed class ViewDefinition
{
    public required string CollectionId { get; init; }
    public string? SortColumn { get; init; }
    public bool SortAscending { get; init; } = true;
    public IReadOnlyList<FilterSpec> Filters { get; init; } = [];
    public IReadOnlyList<string>? Fields { get; init; }
}