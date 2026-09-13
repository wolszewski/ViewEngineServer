using System.Runtime.CompilerServices;

namespace LiveViewEngine.Core;

/// <summary>
/// A set of field indexes, sized to the schema it was built for. Only built on cold paths
/// (subscribe, filter-set creation), so it stores its words on the heap rather than inline and
/// imposes no ceiling on how many fields a collection may have.
/// </summary>
public readonly struct FieldMask : IEquatable<FieldMask>
{
    // null means "no bits set at all" — default(FieldMask) is the empty mask, which the row-delete
    // path and FilterSet.None both rely on.
    private readonly ulong[]? _words;

    // MutationPropagator hashes viewport group keys once per subscriber per mutation, so the hash
    // is computed here (on the cold construction path) rather than walking the words each time.
    private readonly int _hash;

    private FieldMask(ulong[]? words, int hash)
    {
        _words = words;
        _hash = hash;
    }

    public static FieldMask From(ReadOnlySpan<int> indexes, int fieldCount)
    {
        int wordCount = (fieldCount + 63) >> 6;
        if (wordCount == 0)
        {
            return default;
        }

        var words = new ulong[wordCount];
        foreach (var col in indexes)
        {
            if (col < 0) { continue; }
            words[col >> 6] |= 1UL << (col & 63);
        }

        return new FieldMask(words, ComputeHash(words));
    }

    // Hashes only up to the last set bit, and yields 0 when nothing is set, so a sized-but-empty
    // mask hashes the same as default(FieldMask) — which Equals also treats as equal.
    private static int ComputeHash(ulong[] words)
    {
        int last = words.Length - 1;
        while (last >= 0 && words[last] == 0) { last--; }
        if (last < 0) { return 0; }

        var hash = new HashCode();
        for (int i = 0; i <= last; i++) { hash.Add(words[i]); }
        return hash.ToHashCode();
    }

    public bool this[int fieldIndex]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            int word = fieldIndex >> 6;
            return _words is not null
                && (uint)word < (uint)_words.Length
                && ((_words[word] >> (fieldIndex & 63)) & 1UL) != 0;
        }
    }

    public bool IsEmpty
    {
        get
        {
            if (_words is null) { return true; }
            foreach (var word in _words)
            {
                if (word != 0) { return false; }
            }
            return true;
        }
    }

    /// <summary>
    /// True if any of the mutated columns is in this set. Takes the mutation's changed columns
    /// directly so the hot path never has to build a mask of its own.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsAny(ReadOnlySpan<KeyValuePair<int, string?>> changedColumns)
    {
        if (_words is null) { return false; }

        for (int i = 0; i < changedColumns.Length; i++)
        {
            if (this[changedColumns[i].Key]) { return true; }
        }

        return false;
    }

    // Masks built for different schemas can meet here (FilterSet.None is empty and width-less), so
    // compare and hash as if both were zero-padded to the same length.
    public bool Equals(FieldMask other)
    {
        // Subscribers sharing a projection share the mask instance, so the reference check carries
        // the grouping path; the word-wise compare is the fallback for equal-but-distinct masks.
        if (ReferenceEquals(_words, other._words)) { return true; }
        if (_hash != other._hash) { return false; }

        int length = Math.Max(_words?.Length ?? 0, other._words?.Length ?? 0);
        for (int i = 0; i < length; i++)
        {
            if (WordAt(_words, i) != WordAt(other._words, i))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode() => _hash;

    private static ulong WordAt(ulong[]? words, int index) =>
        words is not null && index < words.Length ? words[index] : 0;

    public override bool Equals(object? obj) => obj is FieldMask other && Equals(other);

    public static bool operator ==(FieldMask left, FieldMask right) => left.Equals(right);

    public static bool operator !=(FieldMask left, FieldMask right) => !left.Equals(right);
}
