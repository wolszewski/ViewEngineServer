using LiveViewEngine.Core.Data;
using LiveViewEngine.Core.DataIngest;
using LiveViewEngine.Core.Views;

namespace LiveViewEngine.Core.Runtime;

// Owns everything specific to server-side filtering: whether it's enabled, the registered
// filter-preset registry, and resolving a ViewDefinition's effective filter set (explicit
// Filters merged with any FilterPresetId). CollectionRuntime only calls through this interface -
// it has no built-in knowledge of what "filtering" means beyond "ask the capability".
public interface IFilteringCapability
{
    IngestResult RegisterPreset(string filterPresetId, IReadOnlyList<FilterSpec> filters, CollectionSchema schema);

    // Filters that would actually apply for this view (explicit Filters merged with any
    // registered FilterPresetId), and whether FilterPresetId refers to an unregistered preset.
    (IReadOnlyList<FilterSpec> Filters, bool UnknownPreset) ResolveEffectiveFilters(ViewDefinition view);

    (string Reason, string Message)? Validate(IReadOnlyList<FilterSpec> effectiveFilters);
}

public sealed class FilteringCapability(bool enabled) : IFilteringCapability
{
    private readonly Dictionary<string, IReadOnlyList<FilterSpec>> _presets = new();

    public IngestResult RegisterPreset(string filterPresetId, IReadOnlyList<FilterSpec> filters, CollectionSchema schema)
    {
        if (_presets.ContainsKey(filterPresetId))
        {
            return IngestResult.Fail(
                $"Filter preset '{filterPresetId}' is already registered and cannot be overwritten.");
        }

        foreach (var filter in filters)
        {
            if (schema.GetFieldIndex(filter.FieldName) < 0)
            {
                return IngestResult.Fail($"Unknown field '{filter.FieldName}' for collection '{schema.CollectionName}'.");
            }
        }

        _presets[filterPresetId] = filters;
        return IngestResult.Ok();
    }

    public (IReadOnlyList<FilterSpec> Filters, bool UnknownPreset) ResolveEffectiveFilters(ViewDefinition view)
    {
        if (view.FilterPresetId is null)
        {
            return (view.Filters, false);
        }

        if (!_presets.TryGetValue(view.FilterPresetId, out var baseFilters))
        {
            return (view.Filters, true);
        }

        if (baseFilters.Count == 0)
        {
            return (view.Filters, false);
        }

        IReadOnlyList<FilterSpec> combined = view.Filters.Count > 0 ? [.. baseFilters, .. view.Filters] : baseFilters;
        return (combined, false);
    }

    public (string Reason, string Message)? Validate(IReadOnlyList<FilterSpec> effectiveFilters)
    {
        if (effectiveFilters.Count > 0 && !enabled)
        {
            return ("filtering_not_enabled", "Server-side filtering is not enabled for this deployment.");
        }

        return null;
    }
}
