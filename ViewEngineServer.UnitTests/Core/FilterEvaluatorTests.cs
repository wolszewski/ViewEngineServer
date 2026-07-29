using ViewEngineServer.Core;

namespace ViewEngineServer.UnitTests.Indexing;

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
    public void PassesAll_AllFiltersMatch_ReturnsTrue()
    {
        var row = new string?[] { "r1", "Alice", "50" };
        var filters = new List<FilterSpec>
        {
            new("name", FilterOperator.Eq, "Alice"),
            new("score", FilterOperator.Gte, "40")
        };
        Assert.True(FilterEvaluator.PassesAll(row, [1, 2], filters));
    }

    [Fact]
    public void PassesAll_OneFilterFails_ReturnsFalse()
    {
        var row = new string?[] { "r1", "Alice", "30" };
        var filters = new List<FilterSpec>
        {
            new("name", FilterOperator.Eq, "Alice"),
            new("score", FilterOperator.Gte, "40")
        };
        Assert.False(FilterEvaluator.PassesAll(row, [1, 2], filters));
    }
}
