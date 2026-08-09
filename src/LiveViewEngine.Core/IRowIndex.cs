namespace LiveViewEngine.Core;

internal interface IRowIndex
{
    int Count { get; }
    int GetByIndex(int position);
    int IndexOf(int rowIndex);
    void Take(int startIndex, Span<int> destination);
}
