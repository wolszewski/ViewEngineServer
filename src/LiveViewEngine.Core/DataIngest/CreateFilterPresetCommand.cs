namespace LiveViewEngine.Core.DataIngest;

public sealed class CreateFilterPresetCommand : IngestCommand
{
    public required string FilterPresetId { get; init; }
    public IReadOnlyList<FilterSpec> Filters { get; init; } = [];
}
