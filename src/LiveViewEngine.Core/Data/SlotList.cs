namespace LiveViewEngine.Core.Data;

public sealed class SlotList<T>
    where T : class
{
    private readonly Stack<int> _freeIndexes = new();
    private readonly List<T?> _data = new();

    public T? this[int index]
    {
        get => _data[index];
    }

    public int Capacity => _data.Count;
    public int LiveCount => _data.Count - _freeIndexes.Count;

    public int Add(T item)
    {
        int rowIndex;
        if (_freeIndexes.Count > 0)
        {
            rowIndex = _freeIndexes.Pop();
            _data[rowIndex] = item;
        }
        else
        {
            rowIndex = _data.Count;
            _data.Add(item);
        }
        return rowIndex;
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _data.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (_data[index] is null)
        {
            throw new InvalidOperationException($"Slot at index {index} is already empty.");
        }

        _data[index] = null;
        _freeIndexes.Push(index);
    }
}