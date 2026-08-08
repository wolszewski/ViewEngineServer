namespace LiveViewEngine.Core.Data;

public class SlotList<T>
{
    private readonly Stack<int> _freeIndexes = new();
    private readonly List<T> _data = new();
    
    public T this[int index]
    {
        get => _data[index];
    }
    
    public  int Count => _data.Count;

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
        _data[index] = default!;
        _freeIndexes.Push(index);
    }
}