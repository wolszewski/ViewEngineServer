using LiveViewEngine.Collections;

namespace LiveViewEngine.Core;

internal sealed class FilteredDataIndex
{
    private readonly NodeArrayTree<SortIndex.RowComparer> _index;

    internal FilteredDataIndex(SortIndex.RowComparer comparer)
    {
        _index = new NodeArrayTree<SortIndex.RowComparer>(comparer);
    }

    internal FilteredDataIndex(SortIndex.RowComparer comparer, IEnumerable<int> rows)
    {
        _index = new NodeArrayTree<SortIndex.RowComparer>(comparer);
        foreach (int rowIndex in rows)
        {
            _index.Insert(rowIndex);
        }
    }

    internal int Count => _index.Count;

    internal int Insert(int rowIndex) => _index.Insert(rowIndex);

    internal int TryDelete(int rowIndex) => _index.TryDelete(rowIndex);

    internal bool Contains(int rowIndex) => _index.Contains(rowIndex);

    internal int IndexOf(int rowIndex) => _index.IndexOf(rowIndex);

    internal int GetByIndex(int k) => _index.GetByIndex(k);

    internal NodeArrayTree<SortIndex.RowComparer>.TreeCursor GetCursor(int startIndex) => _index.GetCursor(startIndex);
}
