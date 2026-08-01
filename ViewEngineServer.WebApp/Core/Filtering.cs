namespace ViewEngineServer.WebApp.Core;


public enum FilterOperator { Eq, NotEq, Gt, Gte, Lt, Lte, Contains }

public sealed record FilterSpec(string FieldName, FilterOperator Operator, string? Value);


public static class FilterEvaluator
{
    public static bool Matches(string? fieldValue, FilterSpec filter)
    {
        return filter.Operator switch
        {
            FilterOperator.Eq => CompareValues(fieldValue, filter.Value) == 0,
            FilterOperator.NotEq => CompareValues(fieldValue, filter.Value) != 0,
            FilterOperator.Gt => CompareValues(fieldValue, filter.Value) > 0,
            FilterOperator.Gte => CompareValues(fieldValue, filter.Value) >= 0,
            FilterOperator.Lt => CompareValues(fieldValue, filter.Value) < 0,
            FilterOperator.Lte => CompareValues(fieldValue, filter.Value) <= 0,
            FilterOperator.Contains => fieldValue?.Contains(filter.Value ?? string.Empty,
                           StringComparison.OrdinalIgnoreCase) == true,
            _ => false
        };
    }

    public static bool PassesAll(string?[] rowValues, int[] fieldIndexes, IReadOnlyList<FilterSpec> filters)
    {
        for (int i = 0; i < filters.Count; i++)
        {
            var fi = fieldIndexes[i];
            if (fi < 0)
            {
                continue;
            }

            if (!Matches(rowValues[fi], filters[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static int CompareValues(string? a, string? b)
    {
        if (a is null && b is null)
        {
            return 0;
        }

        if (a is null)
        {
            return -1;
        }

        if (b is null)
        {
            return 1;
        }

        return string.Compare(a, b, StringComparison.Ordinal);
    }
}
