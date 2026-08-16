using LiveViewEngine.Collections;

namespace LiveViewEngine.Core;

internal sealed class FilteredDataIndex : IRowIndex
{
    private readonly NodeArrayTree<SortIndex.RowComparer> _index;

    internal FilteredDataIndex(SortIndex.RowComparer comparer, IEnumerable<int> rows)
    {
        _index = new NodeArrayTree<SortIndex.RowComparer>(comparer);
        foreach (int rowIndex in rows)
        {
            _index.Insert(rowIndex);
        }
    }

    public int Count => _index.Count;

    internal int Insert(int rowIndex) => _index.Insert(rowIndex);

    internal int TryDelete(int rowIndex) => _index.TryDelete(rowIndex);

    public int IndexOf(int rowIndex) => _index.IndexOf(rowIndex);

    public int GetByIndex(int k) => _index.GetByIndex(k);

    public void Take(int startIndex, Span<int> destination) => _index.Take(startIndex, destination);

    public void TakeReverse(int startIndex, Span<int> destination) => _index.TakeReverse(startIndex, destination);
}
