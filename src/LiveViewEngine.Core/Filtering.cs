using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core;

public enum FilterOperator { Eq, NotEq, Gt, Gte, Lt, Lte, Contains }

public sealed record FilterSpec(string FieldName, FilterOperator Operator, string? Value);

public static class FilterEvaluator
{
    public static bool Matches(string? fieldValue, FilterSpec filter, FieldDefinition? fieldDefinition = null)
    {
        var scalarType = fieldDefinition?.Type ?? ScalarFieldType.String;

        if (filter.Operator == FilterOperator.Contains)
        {
            return scalarType == ScalarFieldType.String &&
                   fieldValue is string s &&
                   s.Contains(filter.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return CompareString(fieldValue, filter.Value, filter.Operator);
    }

    internal static bool CompareString(string? left, string? right, FilterOperator filterOperator)
    {
        if (left is null && right is null)
        {
            return filterOperator is FilterOperator.Eq or FilterOperator.Lte or FilterOperator.Gte;
        }

        if (left is null || right is null)
        {
            return filterOperator == FilterOperator.NotEq;
        }

        var comparison = string.Compare(left, right, StringComparison.Ordinal);
        return EvaluateComparison(comparison, filterOperator);
    }

    internal static bool EvaluateComparison(int comparison, FilterOperator filterOperator)
    {
        return filterOperator switch
        {
            FilterOperator.Eq => comparison == 0,
            FilterOperator.NotEq => comparison != 0,
            FilterOperator.Gt => comparison > 0,
            FilterOperator.Gte => comparison >= 0,
            FilterOperator.Lt => comparison < 0,
            FilterOperator.Lte => comparison <= 0,
            _ => false
        };
    }
}
