using LiveViewEngine.Collections;

namespace LiveViewEngine.Core;

internal sealed class FilteredSortIndex
{
    private readonly NodeArrayTree<SortIndex.RowComparer> _tree;

    internal FilteredSortIndex(SortIndex.RowComparer comparer)
    {
        _tree = new NodeArrayTree<SortIndex.RowComparer>(comparer);
    }

    internal int Count => _tree.Count;

    internal int Insert(int rowIndex) => _tree.Insert(rowIndex);

    internal int TryDelete(int rowIndex) => _tree.TryDelete(rowIndex);

    internal bool Contains(int rowIndex) => _tree.Contains(rowIndex);

    internal int IndexOf(int rowIndex) => _tree.IndexOf(rowIndex);

    internal int GetByIndex(int k) => _tree.GetByIndex(k);

    internal NodeArrayTree<SortIndex.RowComparer>.TreeCursor GetCursor(int startIndex) => _tree.GetCursor(startIndex);
}
