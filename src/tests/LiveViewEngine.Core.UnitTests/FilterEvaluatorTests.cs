using System.Globalization;
using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.UnitTests;

public class FilterEvaluatorTests
{

    [Fact]
    public void Eq_MatchingStringValue_ReturnsTrue()
    {
        var filter = new FilterSpec("f", FilterOperator.Eq, "hello");
        Assert.True(FilterEvaluator.Matches("hello", filter));
    }

    [Fact]
    public void Eq_NonMatchingValue_ReturnsFalse()
    {
        var filter = new FilterSpec("f", FilterOperator.Eq, "hello");
        Assert.False(FilterEvaluator.Matches("world", filter));
    }

    [Fact]
    public void Eq_BothNull_ReturnsTrue()
    {
        var filter = new FilterSpec("f", FilterOperator.Eq, null);
        Assert.True(FilterEvaluator.Matches(null, filter));
    }

    [Fact]
    public void Eq_MatchingInteger_ReturnsTrue()
    {
        var filter = new FilterSpec("f", FilterOperator.Eq, "42");
        Assert.True(FilterEvaluator.Matches("42", filter));
    }


    [Fact]
    public void NotEq_DifferentValues_ReturnsTrue()
    {
        var filter = new FilterSpec("f", FilterOperator.NotEq, "a");
        Assert.True(FilterEvaluator.Matches("b", filter));
    }

    [Fact]
    public void NotEq_SameValues_ReturnsFalse()
    {
        var filter = new FilterSpec("f", FilterOperator.NotEq, "a");
        Assert.False(FilterEvaluator.Matches("a", filter));
    }


    [Theory]
    [InlineData("5", "3", true)]
    [InlineData("3", "3", false)]
    [InlineData("1", "3", false)]
    public void Gt_Strings(string fieldValue, string filterValue, bool expected)
    {
        var filter = new FilterSpec("f", FilterOperator.Gt, filterValue);
        Assert.Equal(expected, FilterEvaluator.Matches(fieldValue, filter));
    }

    [Theory]
    [InlineData("5", "3", true)]
    [InlineData("3", "3", true)]
    [InlineData("1", "3", false)]
    public void Gte_Strings(string fieldValue, string filterValue, bool expected)
    {
        var filter = new FilterSpec("f", FilterOperator.Gte, filterValue);
        Assert.Equal(expected, FilterEvaluator.Matches(fieldValue, filter));
    }

    [Theory]
    [InlineData("1", "3", true)]
    [InlineData("3", "3", false)]
    [InlineData("5", "3", false)]
    public void Lt_Strings(string fieldValue, string filterValue, bool expected)
    {
        var filter = new FilterSpec("f", FilterOperator.Lt, filterValue);
        Assert.Equal(expected, FilterEvaluator.Matches(fieldValue, filter));
    }

    [Theory]
    [InlineData("1", "3", true)]
    [InlineData("3", "3", true)]
    [InlineData("5", "3", false)]
    public void Lte_Strings(string fieldValue, string filterValue, bool expected)
    {
        var filter = new FilterSpec("f", FilterOperator.Lte, filterValue);
        Assert.Equal(expected, FilterEvaluator.Matches(fieldValue, filter));
    }

    [Fact]
    public void Gt_UsesTypedNumericComparison_WhenFieldIsDeclaredAsInt32()
    {
        var schema = new CollectionSchema("scores", ["score"], [ScalarFieldType.Int32]);
        var filter = new FilterSpec("score", FilterOperator.Gt, "3");

        Assert.True(FilterEvaluator.Matches("5", filter, schema.GetFieldDefinition("score")));
        Assert.False(FilterEvaluator.Matches("2", filter, schema.GetFieldDefinition("score")));
    }

    [Fact]
    public void Lte_UsesTypedDateComparison_WhenFieldIsDeclaredAsDateTime()
    {
        var schema = new CollectionSchema("events", ["createdOn"], [ScalarFieldType.DateTime]);
        var filter = new FilterSpec("createdOn", FilterOperator.Lte, "2025-01-15T00:00:00Z");

        Assert.True(FilterEvaluator.Matches("2025-01-10T00:00:00Z", filter, schema.GetFieldDefinition("createdOn")));
        Assert.False(FilterEvaluator.Matches("2025-01-20T00:00:00Z", filter, schema.GetFieldDefinition("createdOn")));
    }

    [Fact]
    public void Gte_UsesTypedDateOnlyComparison_WhenFieldIsDeclaredAsDateOnly()
    {
        var schema = new CollectionSchema("events", ["day"], [ScalarFieldType.DateOnly]);
        var filter = new FilterSpec("day", FilterOperator.Gte, "2025-01-15");

        Assert.True(FilterEvaluator.Matches("2025-01-16", filter, schema.GetFieldDefinition("day")));
        Assert.False(FilterEvaluator.Matches("2025-01-14", filter, schema.GetFieldDefinition("day")));
    }

    [Fact]
    public void TypedConverters_Parses_AllSupportedScalarTypes()
    {
        Assert.Equal(42, ScalarValueConverter.ParseInt32("42"));
        Assert.Equal(9223372036854775807L, ScalarValueConverter.ParseInt64("9223372036854775807"));
        Assert.Equal(3.5d, ScalarValueConverter.ParseDouble("3.5"));
        Assert.Equal(12.75m, ScalarValueConverter.ParseDecimal("12.75"));
        Assert.Equal(new DateOnly(2025, 1, 15), ScalarValueConverter.ParseDateOnly("2025-01-15"));
        Assert.Equal(DateTime.Parse("2025-01-15T12:34:56Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ScalarValueConverter.ParseDateTime("2025-01-15T12:34:56Z"));
        Assert.Equal(DateTimeOffset.Parse("2025-01-15T12:34:56+02:00", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ScalarValueConverter.ParseDateTimeOffset("2025-01-15T12:34:56+02:00"));

        Assert.Equal(true, ScalarValueConverter.ParseBoolean("true"));
        Assert.Equal(false, ScalarValueConverter.ParseBoolean("false"));
        Assert.Null(ScalarValueConverter.ParseBoolean("1"));
        Assert.Null(ScalarValueConverter.ParseBoolean("0"));
        Assert.Null(ScalarValueConverter.ParseBoolean("TRUE"));
    }

    [Theory]
    [InlineData(ScalarFieldType.Int32, "not-a-number")]
    [InlineData(ScalarFieldType.Int64, "abc")]
    [InlineData(ScalarFieldType.Double, "not-a-double")]
    [InlineData(ScalarFieldType.Decimal, "not-a-decimal")]
    [InlineData(ScalarFieldType.DateOnly, "not-a-date")]
    [InlineData(ScalarFieldType.DateTime, "not-a-datetime")]
    [InlineData(ScalarFieldType.DateTimeOffset, "not-an-offset")]
    [InlineData(ScalarFieldType.Boolean, "not-a-bool")]
    public void TypedConverters_Fails_ForInvalidValue_ForAllTypedScalars(ScalarFieldType type, string raw)
    {
        object? result = type switch
        {
            ScalarFieldType.Boolean => ScalarValueConverter.ParseBoolean(raw),
            ScalarFieldType.Int32 => ScalarValueConverter.ParseInt32(raw),
            ScalarFieldType.Int64 => ScalarValueConverter.ParseInt64(raw),
            ScalarFieldType.Double => ScalarValueConverter.ParseDouble(raw),
            ScalarFieldType.Decimal => ScalarValueConverter.ParseDecimal(raw),
            ScalarFieldType.DateOnly => ScalarValueConverter.ParseDateOnly(raw),
            ScalarFieldType.DateTime => ScalarValueConverter.ParseDateTime(raw),
            ScalarFieldType.DateTimeOffset => ScalarValueConverter.ParseDateTimeOffset(raw),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };

        Assert.Null(result);
    }

    [Fact]
    public void Contains_SubstringPresent_ReturnsTrue()
    {
        var filter = new FilterSpec("f", FilterOperator.Contains, "ell");
        Assert.True(FilterEvaluator.Matches("hello", filter));
    }

    [Fact]
    public void Contains_CaseInsensitive_ReturnsTrue()
    {
        var filter = new FilterSpec("f", FilterOperator.Contains, "ELL");
        Assert.True(FilterEvaluator.Matches("hello", filter));
    }

    [Fact]
    public void Contains_SubstringAbsent_ReturnsFalse()
    {
        var filter = new FilterSpec("f", FilterOperator.Contains, "xyz");
        Assert.False(FilterEvaluator.Matches("hello", filter));
    }

    [Fact]
    public void Contains_NullFieldValue_ReturnsFalse()
    {
        var filter = new FilterSpec("f", FilterOperator.Contains, "a");
        Assert.False(FilterEvaluator.Matches(null, filter));
    }

    [Fact]
    public void Contains_NonStringScalarField_ReturnsFalse()
    {
        var schema = new CollectionSchema("scores", ["score"], [ScalarFieldType.Int32]);
        var filter = new FilterSpec("score", FilterOperator.Contains, "2");

        Assert.False(FilterEvaluator.Matches("42", filter, schema.GetFieldDefinition("score")));
    }
}
