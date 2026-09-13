namespace LiveViewEngine.Core.UnitTests;

// FieldMask is sized to the schema's field count rather than a fixed inline buffer, so the word
// boundaries (63/64, 127/128) and widths past the old 128-field ceiling are the interesting cases.
public class FieldMaskTests
{
    private static KeyValuePair<int, string?>[] Changed(params int[] fieldIndexes) =>
        [.. fieldIndexes.Select(i => new KeyValuePair<int, string?>(i, "v"))];

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(255)]
    [InlineData(299)]
    public void Indexer_ReadsBitSetAtAnyPosition(int fieldIndex)
    {
        var mask = FieldMask.From([fieldIndex], fieldCount: 300);

        Assert.True(mask[fieldIndex]);
        Assert.False(mask[fieldIndex + 1]);
        if (fieldIndex > 0)
        {
            Assert.False(mask[fieldIndex - 1]);
        }
    }

    [Fact]
    public void From_SupportsSchemasWiderThanTheOldInlineLimit()
    {
        // AR-08 regression: capacity used to be a fixed 2 words (128 fields), so anything past
        // index 127 threw IndexOutOfRangeException on construction.
        var mask = FieldMask.From([0, 150, 299], fieldCount: 300);

        Assert.True(mask[0]);
        Assert.True(mask[150]);
        Assert.True(mask[299]);
        Assert.False(mask[149]);
        Assert.False(mask[151]);
    }

    [Fact]
    public void Default_IsEmptyAndHasNoBits()
    {
        // FilterSet.None and the row-delete path both build a mask with no schema in scope.
        FieldMask mask = default;

        Assert.True(mask.IsEmpty);
        Assert.False(mask[0]);
        Assert.False(mask[127]);
        Assert.False(mask[5000]);
        Assert.False(mask.ContainsAny(Changed(0, 5000)));
    }

    [Fact]
    public void Indexer_ReturnsFalseBeyondMaskWidth()
    {
        var mask = FieldMask.From([3], fieldCount: 64);

        Assert.True(mask[3]);
        Assert.False(mask[200]);
    }

    [Fact]
    public void From_IgnoresNegativeIndexes()
    {
        // FilterSet.Create passes -1 for filter specs naming an unknown field.
        var mask = FieldMask.From([-1, 7, -1], fieldCount: 64);

        Assert.True(mask[7]);
        Assert.False(mask.IsEmpty);
    }

    [Fact]
    public void IsEmpty_TrueForSizedMaskWithNoBits()
    {
        Assert.True(FieldMask.From([], fieldCount: 300).IsEmpty);
        Assert.False(FieldMask.From([299], fieldCount: 300).IsEmpty);
    }

    [Fact]
    public void ContainsAny_FindsOverlapAnywhereInTheChangedSet()
    {
        var mask = FieldMask.From([10, 200], fieldCount: 300);

        Assert.True(mask.ContainsAny(Changed(10)));
        Assert.True(mask.ContainsAny(Changed(200)));
        Assert.True(mask.ContainsAny(Changed(10, 11, 12)));   // hit on the first entry
        Assert.True(mask.ContainsAny(Changed(1, 2, 200)));    // hit on the last entry
    }

    [Fact]
    public void ContainsAny_FalseWhenNothingOverlaps()
    {
        var mask = FieldMask.From([10, 200], fieldCount: 300);

        Assert.False(mask.ContainsAny(Changed(11, 199, 299)));
        Assert.False(mask.ContainsAny([]));
        Assert.False(mask.ContainsAny(default));
    }

    [Fact]
    public void Equals_TreatsNarrowerAndWiderMasksAsZeroPadded()
    {
        var narrow = FieldMask.From([5], fieldCount: 64);
        var wide = FieldMask.From([5], fieldCount: 300);

        Assert.Equal(narrow, wide);
        Assert.True(narrow == wide);
        Assert.Equal(narrow.GetHashCode(), wide.GetHashCode());
    }

    [Fact]
    public void Equals_DefaultMatchesAnySizedEmptyMask()
    {
        var sized = FieldMask.From([], fieldCount: 300);

        Assert.Equal(default, sized);
        Assert.Equal(default(FieldMask).GetHashCode(), sized.GetHashCode());
    }

    [Fact]
    public void Equals_DistinguishesDifferentBits()
    {
        var a = FieldMask.From([5], fieldCount: 300);
        var b = FieldMask.From([5, 299], fieldCount: 300);

        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }
}
