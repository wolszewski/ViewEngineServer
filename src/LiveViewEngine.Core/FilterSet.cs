using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core;

internal sealed class FilterSet
{
    private static readonly FilterSet None = new([], []);

    private readonly IReadOnlyList<FilterSpec> _specs;
    private readonly int[] _fieldIndexes;
    internal FieldMask Mask { get; }
    internal bool HasFilters => _specs.Count > 0;

    internal static FilterSet Create(IReadOnlyList<FilterSpec> specs, CollectionSchema schema)
    {
        if (specs.Count == 0) { return None; }
        var fieldIndexes = specs.Select(f => schema.GetFieldIndex(f.FieldName)).ToArray();
        return new FilterSet(specs, fieldIndexes);
    }

    internal FilterSet(IReadOnlyList<FilterSpec> specs, int[] fieldIndexes)
    {
        _specs = specs;
        _fieldIndexes = fieldIndexes;
        Mask = FieldMask.From(fieldIndexes.AsSpan());
    }

    internal bool Passes(RowCollection collection, int rowIndex)
    {
        for (var i = 0; i < _specs.Count; i++)
        {
            var fieldIndex = _fieldIndexes[i];
            if (fieldIndex < 0)
            {
                continue;
            }

            if (!FilterEvaluator.Matches(collection.GetValue(rowIndex, fieldIndex), _specs[i]))
            {
                return false;
            }
        }
        return true;
    }
}
