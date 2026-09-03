namespace LiveViewEngine.Core;

internal interface IRowIndex
{
    int Count { get; }
    int GetByIndex(int position);
    int IndexOf(int rowIndex);
    void Take(int startIndex, Span<int> destination);
    void TakeReverse(int startIndex, Span<int> destination);
}

// A row index whose membership can change independently of position mutation (used for
// filtered views layered on top of an IPositionIndex).
internal interface IMutableRowIndex : IRowIndex
{
    int Insert(int rowIndex);
    int TryDelete(int rowIndex);
}
