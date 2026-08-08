using LiveViewEngine.Core;
using Xunit;

namespace LiveViewEngine.Collections.UnitTests;

public class OrderStatisticsTreeTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private readonly struct AscIntComparer : IComparer<int>
    {
        public int Compare(int x, int y) => x.CompareTo(y);
    }

    private readonly struct DescIntComparer : IComparer<int>
    {
        public int Compare(int x, int y) => y.CompareTo(x);
    }

    private static OrderStatisticsTree<AscIntComparer> Make(params int[] keys)
    {
        var tree = new OrderStatisticsTree<AscIntComparer>(default);
        foreach (var k in keys) { tree.Insert(k); }
        return tree;
    }

    private static List<int> ToList<TComparer>(OrderStatisticsTree<TComparer> tree) where TComparer : IComparer<int>
    {
        var list = new List<int>(tree.Count);
        var cursor = tree.GetCursor(0);
        while (cursor.MoveNext()) { list.Add(cursor.Current); }
        return list;
    }

    // ── Insert ───────────────────────────────────────────────────────────────

    [Fact]
    public void Insert_Single_CountIsOne()
    {
        var tree = Make(42);
        Assert.Equal(1, tree.Count);
    }

    [Fact]
    public void Insert_Ascending_ProducesSortedOrder()
    {
        var tree = Make(1, 2, 3, 4, 5);
        Assert.Equal([1, 2, 3, 4, 5], ToList(tree));
    }

    [Fact]
    public void Insert_Descending_ProducesSortedOrder()
    {
        var tree = Make(5, 4, 3, 2, 1);
        Assert.Equal([1, 2, 3, 4, 5], ToList(tree));
    }

    [Fact]
    public void Insert_Random_ProducesSortedOrder()
    {
        int[] keys = [7, 3, 9, 1, 5, 8, 2, 6, 4, 10];
        var tree = Make(keys);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], ToList(tree));
    }

    [Fact]
    public void Insert_Large_CountAndOrderCorrect()
    {
        var rng = new Random(42);
        var keys = Enumerable.Range(0, 1000).Select(_ => rng.Next(10_000)).Distinct().ToArray();
        var tree = Make(keys);
        Assert.Equal(keys.Length, tree.Count);
        var result = ToList(tree);
        for (int i = 1; i < result.Count; i++)
        {
            Assert.True(result[i - 1] < result[i]);
        }
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_OnlyElement_EmptyTree()
    {
        var tree = Make(1);
        tree.Delete(1);
        Assert.Equal(0, tree.Count);
        Assert.Equal([], ToList(tree));
    }

    [Fact]
    public void Delete_Minimum_OrderMaintained()
    {
        var tree = Make(1, 2, 3, 4, 5);
        tree.Delete(1);
        Assert.Equal([2, 3, 4, 5], ToList(tree));
        Assert.Equal(4, tree.Count);
    }

    [Fact]
    public void Delete_Maximum_OrderMaintained()
    {
        var tree = Make(1, 2, 3, 4, 5);
        tree.Delete(5);
        Assert.Equal([1, 2, 3, 4], ToList(tree));
    }

    [Fact]
    public void Delete_Internal_OrderMaintained()
    {
        var tree = Make(1, 2, 3, 4, 5);
        tree.Delete(3);
        Assert.Equal([1, 2, 4, 5], ToList(tree));
    }

    [Fact]
    public void Delete_AllElements_EmptyTree()
    {
        var tree = Make(3, 1, 2);
        tree.Delete(1);
        tree.Delete(2);
        tree.Delete(3);
        Assert.Equal(0, tree.Count);
        Assert.Equal([], ToList(tree));
    }

    [Fact]
    public void Delete_ReinsertAfterDelete_CorrectOrder()
    {
        var tree = Make(1, 2, 3, 4, 5);
        tree.Delete(3);
        tree.Insert(3);
        Assert.Equal([1, 2, 3, 4, 5], ToList(tree));
    }

    [Fact]
    public void Delete_Large_CountAndOrderCorrect()
    {
        var keys = Enumerable.Range(1, 100).ToArray();
        var tree = Make(keys);
        var rng = new Random(99);
        var toDelete = keys.OrderBy(_ => rng.Next()).Take(50).ToHashSet();
        foreach (var k in toDelete) { tree.Delete(k); }
        var result = ToList(tree);
        var expected = keys.Except(toDelete).OrderBy(x => x).ToList();
        Assert.Equal(expected, result);
    }

    // ── GetByIndex ───────────────────────────────────────────────────────────

    [Fact]
    public void GetByIndex_Zero_ReturnsMinimum()
    {
        var tree = Make(3, 1, 2);
        Assert.Equal(1, tree.GetByIndex(0));
    }

    [Fact]
    public void GetByIndex_Last_ReturnsMaximum()
    {
        var tree = Make(3, 1, 2);
        Assert.Equal(3, tree.GetByIndex(2));
    }

    [Fact]
    public void GetByIndex_Middle_CorrectElement()
    {
        var tree = Make(1, 2, 3, 4, 5);
        Assert.Equal(3, tree.GetByIndex(2));
    }

    [Fact]
    public void GetByIndex_AllPositions_MatchSortedOrder()
    {
        int[] keys = [7, 3, 9, 1, 5];
        var tree = Make(keys);
        int[] expected = [1, 3, 5, 7, 9];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], tree.GetByIndex(i));
        }
    }

    [Fact]
    public void GetByIndex_OutOfRange_Throws()
    {
        var tree = Make(1, 2, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.GetByIndex(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.GetByIndex(-1));
    }

    [Fact]
    public void GetByIndex_EmptyTree_Throws()
    {
        var tree = new OrderStatisticsTree<AscIntComparer>(default);
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.GetByIndex(0));
    }

    // ── IndexOf ──────────────────────────────────────────────────────────────

    [Fact]
    public void IndexOf_ExistingMinimum_ReturnsZero()
    {
        var tree = Make(1, 2, 3);
        Assert.Equal(0, tree.IndexOf(1));
    }

    [Fact]
    public void IndexOf_ExistingMaximum_ReturnsLastPosition()
    {
        var tree = Make(1, 2, 3);
        Assert.Equal(2, tree.IndexOf(3));
    }

    [Fact]
    public void IndexOf_AllElements_CorrectPositions()
    {
        var tree = Make(10, 30, 20, 50, 40);
        Assert.Equal(0, tree.IndexOf(10));
        Assert.Equal(1, tree.IndexOf(20));
        Assert.Equal(2, tree.IndexOf(30));
        Assert.Equal(3, tree.IndexOf(40));
        Assert.Equal(4, tree.IndexOf(50));
    }

    [Fact]
    public void IndexOf_NotPresent_ReturnsNegativeOne()
    {
        var tree = Make(1, 3, 5);
        Assert.Equal(-1, tree.IndexOf(2));
        Assert.Equal(-1, tree.IndexOf(0));
        Assert.Equal(-1, tree.IndexOf(6));
    }

    [Fact]
    public void IndexOf_EmptyTree_ReturnsNegativeOne()
    {
        var tree = new OrderStatisticsTree<AscIntComparer>(default);
        Assert.Equal(-1, tree.IndexOf(1));
    }

    [Fact]
    public void IndexOf_AfterDelete_Decrements()
    {
        var tree = Make(1, 2, 3, 4, 5);
        tree.Delete(2);
        Assert.Equal(1, tree.IndexOf(3)); // was 2, now 1
        Assert.Equal(2, tree.IndexOf(4)); // was 3, now 2
    }

    // ── Cursor ───────────────────────────────────────────────────────────────

    [Fact]
    public void Cursor_FromZero_YieldsAllInOrder()
    {
        var tree = Make(5, 3, 1, 4, 2);
        var cursor = tree.GetCursor(0);
        var result = new List<int>();
        while (cursor.MoveNext()) { result.Add(cursor.Current); }
        Assert.Equal([1, 2, 3, 4, 5], result);
    }

    [Fact]
    public void Cursor_FromMiddle_YieldsCorrectSuffix()
    {
        var tree = Make(1, 2, 3, 4, 5);
        var cursor = tree.GetCursor(2); // start at position 2 = key 3
        var result = new List<int>();
        while (cursor.MoveNext()) { result.Add(cursor.Current); }
        Assert.Equal([3, 4, 5], result);
    }

    [Fact]
    public void Cursor_FromLast_YieldsOneElement()
    {
        var tree = Make(1, 2, 3);
        var cursor = tree.GetCursor(2);
        Assert.True(cursor.MoveNext());
        Assert.Equal(3, cursor.Current);
        Assert.False(cursor.MoveNext());
    }

    [Fact]
    public void Cursor_StartIndexEqualToCount_IsEmpty()
    {
        var tree = Make(1, 2, 3);
        var cursor = tree.GetCursor(3);
        Assert.False(cursor.MoveNext());
    }

    [Fact]
    public void Cursor_StartIndexBeyondCount_IsEmpty()
    {
        var tree = Make(1, 2, 3);
        var cursor = tree.GetCursor(100);
        Assert.False(cursor.MoveNext());
    }

    [Fact]
    public void Cursor_EmptyTree_IsEmpty()
    {
        var tree = new OrderStatisticsTree<AscIntComparer>(default);
        var cursor = tree.GetCursor(0);
        Assert.False(cursor.MoveNext());
    }

    [Fact]
    public void Cursor_SingleElement_YieldsOneElement()
    {
        var tree = Make(42);
        var cursor = tree.GetCursor(0);
        Assert.True(cursor.MoveNext());
        Assert.Equal(42, cursor.Current);
        Assert.False(cursor.MoveNext());
    }

    [Fact]
    public void Cursor_PageWindow_CorrectSlice()
    {
        var tree = Make(Enumerable.Range(1, 100).ToArray());
        int startIndex = 30, pageSize = 20;
        var cursor = tree.GetCursor(startIndex);
        var page = new List<int>();
        while (cursor.MoveNext() && page.Count < pageSize)
        {
            page.Add(cursor.Current);
        }
        Assert.Equal(Enumerable.Range(31, 20).ToList(), page);
    }

    // ── Contains ─────────────────────────────────────────────────────────────

    [Fact]
    public void Contains_PresentKey_True()
    {
        var tree = Make(1, 2, 3);
        Assert.True(tree.Contains(2));
    }

    [Fact]
    public void Contains_AbsentKey_False()
    {
        var tree = Make(1, 2, 3);
        Assert.False(tree.Contains(5));
    }

    // ── Custom comparer ───────────────────────────────────────────────────────

    [Fact]
    public void CustomComparer_DescendingOrder_Respected()
    {
        var tree = new OrderStatisticsTree<DescIntComparer>(default);
        foreach (var k in new[] { 3, 1, 4, 1, 5 }.Distinct()) { tree.Insert(k); }
        var result = ToList(tree);
        Assert.Equal([5, 4, 3, 1], result);
    }

    // ── Count consistency ─────────────────────────────────────────────────────

    [Fact]
    public void Count_InsertsAndDeletes_AlwaysConsistent()
    {
        var tree = new OrderStatisticsTree<AscIntComparer>(default);
        for (int i = 1; i <= 10; i++) { tree.Insert(i); Assert.Equal(i, tree.Count); }
        for (int i = 1; i <= 10; i++) { tree.Delete(i); Assert.Equal(10 - i, tree.Count); }
    }

    // ── Tree structural validity (black-height invariant via in-order check) ──

    [Fact]
    public void AfterManyInsertDelete_OrderAndCountCorrect()
    {
        var rng = new Random(777);
        var inTree = new SortedSet<int>();
        var tree = new OrderStatisticsTree<AscIntComparer>(default);

        for (int round = 0; round < 500; round++)
        {
            int key = rng.Next(200);
            if (inTree.Contains(key))
            {
                tree.Delete(key);
                inTree.Remove(key);
            }
            else
            {
                tree.Insert(key);
                inTree.Add(key);
            }

            Assert.Equal(inTree.Count, tree.Count);
        }

        Assert.Equal(inTree.ToList(), ToList(tree));
    }
}
