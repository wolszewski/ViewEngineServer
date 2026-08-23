namespace LiveViewEngine.Core.Data;

internal sealed class UpdateBuffer
{
    private int[] _fieldIndexes;
    private string?[] _values;

    public UpdateBuffer(int initialCapacity)
    {
        var capacity = Math.Max(1, initialCapacity);
        _fieldIndexes = new int[capacity];
        _values = new string?[capacity];
    }

    public int Count { get; private set; }

    public ReadOnlySpan<int> FieldIndexes => _fieldIndexes.AsSpan(0, Count);

    public void Reset() => Count = 0;

    public void Add(int fieldIndex, string? value)
    {
        EnsureCapacity(Count + 1);
        _fieldIndexes[Count] = fieldIndex;
        _values[Count] = value;
        Count++;
    }

    public int GetFieldIndex(int index) => _fieldIndexes[index];

    public string? GetValue(int index) => _values[index];

    public KeyValuePair<int, string?>[] ToChangedColumnsSnapshot()
    {
        if (Count == 0)
        {
            return Array.Empty<KeyValuePair<int, string?>>();
        }

        var snapshot = new KeyValuePair<int, string?>[Count];
        for (var i = 0; i < Count; i++)
        {
            snapshot[i] = new KeyValuePair<int, string?>(_fieldIndexes[i], _values[i]);
        }

        return snapshot;
    }

    private void EnsureCapacity(int minCapacity)
    {
        if (_fieldIndexes.Length >= minCapacity)
        {
            return;
        }

        var newCapacity = Math.Max(minCapacity, _fieldIndexes.Length * 2);
        Array.Resize(ref _fieldIndexes, newCapacity);
        Array.Resize(ref _values, newCapacity);
    }
}
