namespace LiveViewEngine.Core.Data;

internal sealed class UpdateBuffer
{
    private readonly int[] _fieldIndexes;
    private readonly string?[] _values;
    private readonly ReadOnlyView _view;

    public UpdateBuffer(int initialCapacity)
    {
        if (initialCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCapacity), "Capacity must be greater than zero.");
        }

        _fieldIndexes = new int[initialCapacity];
        _values = new string?[initialCapacity];
        _view = new ReadOnlyView(this);
    }

    public int Count { get; private set; }

    public ReadOnlySpan<int> FieldIndexes => _fieldIndexes.AsSpan(0, Count);
    public IReadOnlyList<KeyValuePair<int, string?>> View => _view;

    public void Reset() => Count = 0;

    public void Add(int fieldIndex, string? value)
    {
        if (Count >= _fieldIndexes.Length)
        {
            throw new InvalidOperationException("UpdateBuffer capacity exceeded for collection schema.");
        }

        _fieldIndexes[Count] = fieldIndex;
        _values[Count] = value;
        Count++;
    }

    public int GetFieldIndex(int index) => _fieldIndexes[index];

    public string? GetValue(int index) => _values[index];

    private sealed class ReadOnlyView(UpdateBuffer owner) : IReadOnlyList<KeyValuePair<int, string?>>
    {
        public int Count => owner.Count;

        public KeyValuePair<int, string?> this[int index] =>
            new(owner._fieldIndexes[index], owner._values[index]);

        public IEnumerator<KeyValuePair<int, string?>> GetEnumerator()
        {
            for (var i = 0; i < owner.Count; i++)
            {
                yield return new KeyValuePair<int, string?>(owner._fieldIndexes[i], owner._values[i]);
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
