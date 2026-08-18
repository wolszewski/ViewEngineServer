using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core;

internal sealed class FilterSet
{
    private static readonly FilterSet None = new([], default);

    private readonly Func<RowCollection, int, bool>[] _matchers;
    internal FieldMask Mask { get; }
    internal bool HasFilters => _matchers.Length > 0;

    internal static FilterSet Create(IReadOnlyList<FilterSpec> specs, CollectionSchema schema)
    {
        if (specs.Count == 0)
        {
            return None;
        }

        var fieldIndexes = new int[specs.Count];
        var matchers = new List<Func<RowCollection, int, bool>>(specs.Count);

        for (var i = 0; i < specs.Count; i++)
        {
            var fieldIndex = schema.GetFieldIndex(specs[i].FieldName);
            fieldIndexes[i] = fieldIndex;
            if (fieldIndex < 0)
            {
                continue;
            }

            var matcher = CompileMatcher(specs[i], schema.GetFieldDefinition(fieldIndex));
            matchers.Add(matcher);
        }

        return new FilterSet(matchers.ToArray(), FieldMask.From(fieldIndexes.AsSpan()));
    }

    private FilterSet(Func<RowCollection, int, bool>[] matchers, FieldMask mask)
    {
        _matchers = matchers;
        Mask = mask;
    }

    internal bool Passes(RowCollection collection, int rowIndex)
    {
        for (var i = 0; i < _matchers.Length; i++)
        {
            if (!_matchers[i](collection, rowIndex))
            {
                return false;
            }
        }

        return true;
    }

    private static Func<RowCollection, int, bool> CompileMatcher(FilterSpec spec, FieldDefinition fieldDefinition)
    {
        var fieldIndex = fieldDefinition.FieldIndex;
        var filterOperator = spec.Operator;

        if (filterOperator == FilterOperator.Contains)
        {
            var filterValue = spec.Value ?? string.Empty;
            return (collection, rowIndex) =>
            {
                var raw = collection.GetValue(rowIndex, fieldIndex);
                return raw is not null && raw.Contains(filterValue, StringComparison.OrdinalIgnoreCase);
            };
        }

        if (fieldDefinition.Type == ScalarFieldType.String)
        {
            var filterValue = spec.Value;
            return (collection, rowIndex) =>
                FilterEvaluator.CompareString(collection.GetValue(rowIndex, fieldIndex), filterValue, filterOperator);
        }

        return fieldDefinition.Type switch
        {
            ScalarFieldType.Int32 => CompileScalarMatcher<int>(fieldIndex, spec, ScalarValueConverter.TryConvertInt32,
                static (c, ri, fi) => c.GetInt32(ri, fi)),
            ScalarFieldType.Int64 => CompileScalarMatcher<long>(fieldIndex, spec, ScalarValueConverter.TryConvertInt64,
                static (c, ri, fi) => c.GetInt64(ri, fi)),
            ScalarFieldType.Double => CompileScalarMatcher<double>(fieldIndex, spec, ScalarValueConverter.TryConvertDouble,
                static (c, ri, fi) => c.GetDouble(ri, fi)),
            ScalarFieldType.Decimal => CompileScalarMatcher<decimal>(fieldIndex, spec, ScalarValueConverter.TryConvertDecimal,
                static (c, ri, fi) => c.GetDecimal(ri, fi)),
            ScalarFieldType.DateOnly => CompileScalarMatcher<DateOnly>(fieldIndex, spec, ScalarValueConverter.TryConvertDateOnly,
                static (c, ri, fi) => c.GetDateOnly(ri, fi)),
            ScalarFieldType.DateTime => CompileScalarMatcher<DateTime>(fieldIndex, spec, ScalarValueConverter.TryConvertDateTime,
                static (c, ri, fi) => c.GetDateTime(ri, fi)),
            ScalarFieldType.DateTimeOffset => CompileScalarMatcher<DateTimeOffset>(fieldIndex, spec, ScalarValueConverter.TryConvertDateTimeOffset,
                static (c, ri, fi) => c.GetDateTimeOffset(ri, fi)),
            _ => (collection, rowIndex) =>
                FilterEvaluator.CompareString(collection.GetValue(rowIndex, fieldIndex), spec.Value, filterOperator)
        };
    }

    private delegate bool TryConvert<T>(string? raw, out T value) where T : struct;

    private static Func<RowCollection, int, bool> CompileScalarMatcher<T>(
        int fieldIndex,
        FilterSpec spec,
        TryConvert<T> converter,
        Func<RowCollection, int, int, T?> typedGetter)
        where T : struct, IComparable<T>
    {
        var filterOperator = spec.Operator;

        if (spec.Value is null)
        {
            return (collection, rowIndex) =>
            {
                collection.ActivateTypedField(fieldIndex);
                var leftValue = typedGetter(collection, rowIndex, fieldIndex);
                if (leftValue is null)
                {
                    return filterOperator is FilterOperator.Eq or FilterOperator.Gte or FilterOperator.Lte;
                }

                return filterOperator == FilterOperator.NotEq;
            };
        }

        if (!converter(spec.Value, out var parsedFilterValue))
        {
            return (collection, rowIndex) =>
                FilterEvaluator.CompareString(collection.GetValue(rowIndex, fieldIndex), spec.Value, filterOperator);
        }

        return (collection, rowIndex) =>
        {
            collection.ActivateTypedField(fieldIndex);
            var leftValue = typedGetter(collection, rowIndex, fieldIndex);
            if (leftValue is null)
            {
                return filterOperator == FilterOperator.NotEq;
            }

            var comparison = leftValue.Value.CompareTo(parsedFilterValue);
            return FilterEvaluator.EvaluateComparison(comparison, filterOperator);
        };
    }
}
