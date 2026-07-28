namespace ViewEngineServer.Core;


public enum FilterOperator { Eq, NotEq, Gt, Gte, Lt, Lte, Contains }

public sealed record FilterSpec(string FieldName, FilterOperator Operator, object? Value);


public static class FilterEvaluator
{
    public static bool Matches(object? fieldValue, FilterSpec filter)
    {
        return filter.Operator switch
        {
            FilterOperator.Eq       => CompareValues(fieldValue, filter.Value) == 0,
            FilterOperator.NotEq    => CompareValues(fieldValue, filter.Value) != 0,
            FilterOperator.Gt       => CompareValues(fieldValue, filter.Value) > 0,
            FilterOperator.Gte      => CompareValues(fieldValue, filter.Value) >= 0,
            FilterOperator.Lt       => CompareValues(fieldValue, filter.Value) < 0,
            FilterOperator.Lte      => CompareValues(fieldValue, filter.Value) <= 0,
            FilterOperator.Contains => fieldValue?.ToString()
                ?.Contains(filter.Value?.ToString() ?? string.Empty,
                           StringComparison.OrdinalIgnoreCase) == true,
            _ => false
        };
    }

    public static bool PassesAll(object?[] rowValues, int[] fieldIndexes, IReadOnlyList<FilterSpec> filters)
    {
        for (int i = 0; i < filters.Count; i++)
        {
            var fi = fieldIndexes[i];
            if (fi < 0) continue;
            if (!Matches(rowValues[fi], filters[i])) return false;
        }
        return true;
    }

    private static int CompareValues(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        try
        {
            if (a is IComparable ca)
                return ca.CompareTo(Convert.ChangeType(b, a.GetType()));
        }
        catch { /* fall through to string compare */ }

        return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }
}
