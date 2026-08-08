using LiveViewEngine.Core.Data;

namespace LiveViewEngine.Core.UnitTests;

public sealed class SlotListTests
{
    [Fact]
    public void AddAfterRemove_ReusesFreedIndex()
    {
        var slots = new SlotList<string>();
        var first = slots.Add("a");
        var second = slots.Add("b");

        slots.RemoveAt(first);
        var reused = slots.Add("c");

        Assert.Equal(first, reused);
        Assert.Equal(2, slots.Capacity);
        Assert.Equal(2, slots.LiveCount);
        Assert.Equal("c", slots[reused]);
        Assert.Equal("b", slots[second]);
    }

    [Fact]
    public void RemoveAt_AlreadyEmpty_Throws()
    {
        var slots = new SlotList<string>();
        var index = slots.Add("a");
        slots.RemoveAt(index);

        Assert.Throws<InvalidOperationException>(() => slots.RemoveAt(index));
    }
}
