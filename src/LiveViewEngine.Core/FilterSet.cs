using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core;

internal sealed class FilterSet : IDisposable
{
    private static readonly FilterSet None = new([], default, null, []);

    private readonly Func<RowCollection, int, bool>[] _matchers;
    private readonly RowCollection? _collection;
    private readonly int[] _referencedFields;
    internal FieldMask Mask { get; }
    internal bool HasFilters => _matchers.Length > 0;

    internal static FilterSet Create(
        IReadOnlyList<FilterSpec> specs,
        CollectionSchema schema,
        RowCollection? collection = null,
        TypedColumnKeepAlive keepAlive = TypedColumnKeepAlive.WhenReferencedByIndexes)
    {
        if (specs.Count == 0)
        {
            return None;
        }

        var fieldIndexes = new int[specs.Count];
        var matchers = new Func<RowCollection, int, bool>[specs.Count];
        var matcherCount = 0;
        int[]? referencedFields = null;
        var referencedFieldCount = 0;

        for (var i = 0; i < specs.Count; i++)
        {
            var fieldIndex = schema.GetFieldIndex(specs[i].FieldName);
            fieldIndexes[i] = fieldIndex;
            if (fieldIndex < 0)
            {
                continue;
            }

            var fieldDef = schema.GetFieldDefinition(fieldIndex);
            var activateNow = keepAlive == TypedColumnKeepAlive.WhenReferencedByIndexesAndFilters
                && collection is not null
                && fieldDef.Type is not (ScalarFieldType.String or ScalarFieldType.Boolean);

            if (activateNow)
            {
                collection!.AddTypedFieldRef(fieldIndex);
                referencedFields ??= new int[specs.Count];
                referencedFields[referencedFieldCount++] = fieldIndex;
            }

            matchers[matcherCount++] = CompileMatcher(specs[i], fieldDef, keepAlive);
        }

        return new FilterSet(
            matcherCount == matchers.Length ? matchers : [.. matchers.AsSpan(0, matcherCount)],
            FieldMask.From(fieldIndexes.AsSpan()),
            keepAlive == TypedColumnKeepAlive.WhenReferencedByIndexesAndFilters ? collection : null,
            referencedFieldCount == 0 ? [] : [.. referencedFields!.AsSpan(0, referencedFieldCount)]);
    }

    private FilterSet(Func<RowCollection, int, bool>[] matchers, FieldMask mask, RowCollection? collection, int[] referencedFields)
    {
        _matchers = matchers;
        Mask = mask;
        _collection = collection;
        _referencedFields = referencedFields;
    }

    public void Dispose()
    {
        if (_collection is null)
        {
            return;
        }

        foreach (var fieldIndex in _referencedFields)
        {
            _collection.ReleaseTypedFieldRef(fieldIndex);
        }
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

    private static Func<RowCollection, int, bool> CompileMatcher(
        FilterSpec spec,
        FieldDefinition fieldDefinition,
        TypedColumnKeepAlive keepAlive)
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

        if (fieldDefinition.Type is ScalarFieldType.Boolean)
        {
            var filterValue = ScalarValueConverter.TryConvertBoolean(spec.Value, out var boolValue)
                ? ScalarValueConverter.FormatBoolean(boolValue)
                : spec.Value;
            return (collection, rowIndex) =>
                FilterEvaluator.CompareString(collection.GetValue(rowIndex, fieldIndex), filterValue, filterOperator);
        }

        if (fieldDefinition.Type is ScalarFieldType.String)
        {
            var filterValue = spec.Value;
            return (collection, rowIndex) =>
                FilterEvaluator.CompareString(collection.GetValue(rowIndex, fieldIndex), filterValue, filterOperator);
        }

        return fieldDefinition.Type switch
        {
            ScalarFieldType.Int32 => CompileScalarMatcher<int>(fieldIndex, spec, ScalarValueConverter.TryConvertInt32,
                static (c, ri, fi) => c.GetInt32(ri, fi), keepAlive),
            ScalarFieldType.Int64 => CompileScalarMatcher<long>(fieldIndex, spec, ScalarValueConverter.TryConvertInt64,
                static (c, ri, fi) => c.GetInt64(ri, fi), keepAlive),
            ScalarFieldType.Double => CompileScalarMatcher<double>(fieldIndex, spec, ScalarValueConverter.TryConvertDouble,
                static (c, ri, fi) => c.GetDouble(ri, fi), keepAlive),
            ScalarFieldType.Decimal => CompileScalarMatcher<decimal>(fieldIndex, spec, ScalarValueConverter.TryConvertDecimal,
                static (c, ri, fi) => c.GetDecimal(ri, fi), keepAlive),
            ScalarFieldType.DateOnly => CompileScalarMatcher<DateOnly>(fieldIndex, spec, ScalarValueConverter.TryConvertDateOnly,
                static (c, ri, fi) => c.GetDateOnly(ri, fi), keepAlive),
            ScalarFieldType.DateTime => CompileScalarMatcher<DateTime>(fieldIndex, spec, ScalarValueConverter.TryConvertDateTime,
                static (c, ri, fi) => c.GetDateTime(ri, fi), keepAlive),
            ScalarFieldType.DateTimeOffset => CompileScalarMatcher<DateTimeOffset>(fieldIndex, spec, ScalarValueConverter.TryConvertDateTimeOffset,
                static (c, ri, fi) => c.GetDateTimeOffset(ri, fi), keepAlive),
            _ => (collection, rowIndex) =>
                FilterEvaluator.CompareString(collection.GetValue(rowIndex, fieldIndex), spec.Value, filterOperator)
        };
    }

    private delegate bool TryConvert<T>(string? raw, out T value) where T : struct;

    private static Func<RowCollection, int, bool> CompileScalarMatcher<T>(
        int fieldIndex,
        FilterSpec spec,
        TryConvert<T> converter,
        Func<RowCollection, int, int, T?> typedGetter,
        TypedColumnKeepAlive keepAlive)
        where T : struct, IComparable<T>
    {
        var filterOperator = spec.Operator;

        T? GetValue(RowCollection collection, int rowIndex)
        {
            if (collection.IsTypedFieldActivated(fieldIndex))
            {
                return typedGetter(collection, rowIndex, fieldIndex);
            }

            return converter(collection.GetValue(rowIndex, fieldIndex), out var v) ? v : null;
        }

        if (spec.Value is null)
        {
            return (collection, rowIndex) =>
            {
                var leftValue = GetValue(collection, rowIndex);
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

        var captured = parsedFilterValue;
        return (collection, rowIndex) =>
        {
            var leftValue = GetValue(collection, rowIndex);
            if (leftValue is null)
            {
                return filterOperator == FilterOperator.NotEq;
            }

            return FilterEvaluator.EvaluateComparison(leftValue.Value.CompareTo(captured), filterOperator);
        };
    }
}
