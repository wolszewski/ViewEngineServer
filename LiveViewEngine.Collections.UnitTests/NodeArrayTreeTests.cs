using LiveViewEngine.Collections;
using Xunit;

namespace LiveViewEngine.Collections.UnitTests;

public class NodeArrayTreeTests
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

    private static NodeArrayTree<AscIntComparer> Make(params int[] keys)
    {
        var tree = new NodeArrayTree<AscIntComparer>(default);
        foreach (var k in keys) { tree.Insert(k); }
        return tree;
    }

    private static List<int> ToList<TComparer>(NodeArrayTree<TComparer> tree) where TComparer : IComparer<int>
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
    public void Insert_Multiple_Ascending_InOrder()
    {
        var tree = Make(3, 1, 4, 5, 9, 2, 6);
        Assert.Equal([1, 2, 3, 4, 5, 6, 9], ToList(tree));
    }

    [Fact]
    public void Insert_Descending_SortedAscending()
    {
        var tree = Make(5, 4, 3, 2, 1);
        Assert.Equal([1, 2, 3, 4, 5], ToList(tree));
    }

    [Fact]
    public void Insert_AlreadySorted_SortedAscending()
    {
        var tree = Make(1, 2, 3, 4, 5);
        Assert.Equal([1, 2, 3, 4, 5], ToList(tree));
    }

    // Forces node split: MaxSize=64, insert 65+ unique values to verify splitting works.
    [Fact]
    public void Insert_BeyondNodeCapacity_StaysOrdered()
    {
        var tree = new NodeArrayTree<AscIntComparer>(default);
        for (int i = 1; i <= 200; i++) { tree.Insert(i); }

        Assert.Equal(200, tree.Count);
        var list = ToList(tree);
        for (int i = 0; i < 200; i++) { Assert.Equal(i + 1, list[i]); }
    }

    [Fact]
    public void Insert_RandomOrder_StaysOrdered()
    {
        var rng = new Random(42);
        var keys = Enumerable.Range(1, 500).OrderBy(_ => rng.Next()).ToArray();
        var tree = new NodeArrayTree<AscIntComparer>(default);
        foreach (var k in keys) { tree.Insert(k); }

        var list = ToList(tree);
        for (int i = 0; i < list.Count - 1; i++) { Assert.True(list[i] < list[i + 1]); }
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_OnlyElement_CountZero()
    {
        var tree = Make(1);
        tree.Delete(1);
        Assert.Equal(0, tree.Count);
    }

    [Fact]
    public void Delete_Middle_RemainingOrdered()
    {
        var tree = Make(1, 2, 3, 4, 5);
        tree.Delete(3);
        Assert.Equal([1, 2, 4, 5], ToList(tree));
    }

    [Fact]
    public void Delete_Min_RemainingOrdered()
    {
        var tree = Make(1, 2, 3);
        tree.Delete(1);
        Assert.Equal([2, 3], ToList(tree));
    }

    [Fact]
    public void Delete_Max_RemainingOrdered()
    {
        var tree = Make(1, 2, 3);
        tree.Delete(3);
        Assert.Equal([1, 2], ToList(tree));
    }

    [Fact]
    public void Delete_AllElements_EmptyTree()
    {
        var tree = Make(1, 2, 3, 4, 5);
        foreach (var k in new[] { 1, 2, 3, 4, 5 }) { tree.Delete(k); }
        Assert.Equal(0, tree.Count);
    }

    [Fact]
    public void Delete_BeyondNodeCapacity_StaysOrdered()
    {
        var tree = new NodeArrayTree<AscIntComparer>(default);
        for (int i = 1; i <= 200; i++) { tree.Insert(i); }
        for (int i = 1; i <= 100; i++) { tree.Delete(i); }

        Assert.Equal(100, tree.Count);
        var list = ToList(tree);
        for (int i = 0; i < 100; i++) { Assert.Equal(i + 101, list[i]); }
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

    [Fact]
    public void Contains_EmptyTree_False()
    {
        var tree = Make();
        Assert.False(tree.Contains(1));
    }

    // ── GetByIndex ────────────────────────────────────────────────────────────

    [Fact]
    public void GetByIndex_First_ReturnsMinimum()
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
    public void GetByIndex_Middle_ReturnsCorrect()
    {
        var tree = Make(10, 30, 20, 50, 40);
        Assert.Equal(30, tree.GetByIndex(2));
    }

    [Fact]
    public void GetByIndex_EmptyTree_Throws()
    {
        var tree = Make();
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.GetByIndex(0));
    }

    [Fact]
    public void GetByIndex_OutOfRange_Throws()
    {
        var tree = Make(1, 2, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.GetByIndex(3));
    }

    [Fact]
    public void GetByIndex_LargeTree_Correct()
    {
        var tree = new NodeArrayTree<AscIntComparer>(default);
        for (int i = 0; i < 300; i++) { tree.Insert(i); }
        Assert.Equal(150, tree.GetByIndex(150));
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
        var tree = Make();
        Assert.Equal(-1, tree.IndexOf(1));
    }

    [Fact]
    public void IndexOf_AfterDelete_Decrements()
    {
        var tree = Make(1, 2, 3, 4, 5);
        tree.Delete(2);
        Assert.Equal(1, tree.IndexOf(3));
        Assert.Equal(2, tree.IndexOf(4));
    }

    [Fact]
    public void IndexOf_LargeTree_Correct()
    {
        var tree = new NodeArrayTree<AscIntComparer>(default);
        for (int i = 0; i < 300; i++) { tree.Insert(i); }
        Assert.Equal(200, tree.IndexOf(200));
    }

    // ── Cursor ────────────────────────────────────────────────────────────────

    [Fact]
    public void Cursor_FromZero_IteratesAll()
    {
        var tree = Make(3, 1, 2);
        var cursor = tree.GetCursor(0);
        var result = new List<int>();
        while (cursor.MoveNext()) { result.Add(cursor.Current); }
        Assert.Equal([1, 2, 3], result);
    }

    [Fact]
    public void Cursor_FromMid_IteratesRemainder()
    {
        var tree = Make(1, 2, 3, 4, 5);
        var cursor = tree.GetCursor(2);
        var result = new List<int>();
        while (cursor.MoveNext()) { result.Add(cursor.Current); }
        Assert.Equal([3, 4, 5], result);
    }

    [Fact]
    public void Cursor_StartBeyondEnd_IsEmpty()
    {
        var tree = Make(1, 2, 3);
        var cursor = tree.GetCursor(100);
        Assert.False(cursor.MoveNext());
    }

    [Fact]
    public void Cursor_EmptyTree_IsEmpty()
    {
        var tree = Make();
        var cursor = tree.GetCursor(0);
        Assert.False(cursor.MoveNext());
    }

    [Fact]
    public void Cursor_LargeTree_PageCorrect()
    {
        var tree = new NodeArrayTree<AscIntComparer>(default);
        for (int i = 1; i <= 300; i++) { tree.Insert(i); }

        var cursor = tree.GetCursor(50);
        var page = new List<int>();
        for (int i = 0; i < 10; i++) { cursor.MoveNext(); page.Add(cursor.Current); }
        Assert.Equal(Enumerable.Range(51, 10).ToList(), page);
    }

    [Fact]
    public void Cursor_AcrossNodeBoundary_Seamless()
    {
        // insert exactly 65 items to ensure at least 2 nodes (MaxSize=64)
        var tree = new NodeArrayTree<AscIntComparer>(default);
        for (int i = 1; i <= 65; i++) { tree.Insert(i); }

        var list = ToList(tree);
        Assert.Equal(65, list.Count);
        for (int i = 0; i < 65; i++) { Assert.Equal(i + 1, list[i]); }
    }

    // ── Custom comparer ───────────────────────────────────────────────────────

    [Fact]
    public void CustomComparer_DescendingOrder_Respected()
    {
        var tree = new NodeArrayTree<DescIntComparer>(default);
        foreach (var k in new[] { 3, 1, 4, 5 }) { tree.Insert(k); }
        Assert.Equal([5, 4, 3, 1], ToList(tree));
    }

    // ── Count consistency ─────────────────────────────────────────────────────

    [Fact]
    public void Count_InsertsAndDeletes_AlwaysConsistent()
    {
        var tree = new NodeArrayTree<AscIntComparer>(default);
        for (int i = 1; i <= 10; i++) { tree.Insert(i); Assert.Equal(i, tree.Count); }
        for (int i = 1; i <= 10; i++) { tree.Delete(i); Assert.Equal(10 - i, tree.Count); }
    }

    // ── Mixed insert/delete stress test ──────────────────────────────────────

    [Fact]
    public void AfterManyInsertDelete_OrderAndCountCorrect()
    {
        var rng = new Random(777);
        var inTree = new SortedSet<int>();
        var tree = new NodeArrayTree<AscIntComparer>(default);

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
