using System.Numerics;
using System.Runtime.CompilerServices;

namespace LiveViewEngine.Core;

public struct FieldMask
{
    // Increase WordCapacity to support larger schemas (each word = 64 fields).
    private const int WordCapacity = 2; // 128 fields max

    [InlineArray(WordCapacity)]
    private struct WordBuffer { private ulong _first; }

    private WordBuffer _words;

    public static FieldMask From(IReadOnlyCollection<KeyValuePair<int, string?>> columns)
    {
        var mask = new FieldMask();
        Span<ulong> words = mask._words;
        foreach (var (col, _) in columns)
        {
            words[col >> 6] |= 1UL << (col & 63);
        }
        return mask;
    }

    public static FieldMask From(ReadOnlySpan<int> indexes)
    {
        var mask = new FieldMask();
        Span<ulong> words = mask._words;
        foreach (var col in indexes)
        {
            if (col < 0) { continue; }
            words[col >> 6] |= 1UL << (col & 63);
        }
        return mask;
    }

    public readonly (ulong Low, ulong High) Key =>
        (((ReadOnlySpan<ulong>)_words)[0], ((ReadOnlySpan<ulong>)_words)[1]);

    public readonly bool this[int fieldIndex] =>
        (((ReadOnlySpan<ulong>)_words)[fieldIndex >> 6] >> (fieldIndex & 63) & 1UL) != 0;

    public readonly bool Intersects(in FieldMask other)
    {
        ReadOnlySpan<ulong> a = _words;
        ReadOnlySpan<ulong> b = other._words;
        for (int i = 0; i < WordCapacity; i++)
        {
            if ((a[i] & b[i]) != 0) { return true; }
        }
        return false;
    }

    public readonly bool IsEmpty
    {
        get
        {
            foreach (var word in (ReadOnlySpan<ulong>)_words)
            {
                if (word != 0) { return false; }
            }
            return true;
        }
    }

    public readonly int[] ToIndexes()
    {
        var indexes = new List<int>();
        ReadOnlySpan<ulong> words = _words;
        for (int wordIndex = 0; wordIndex < WordCapacity; wordIndex++)
        {
            ulong word = words[wordIndex];
            while (word != 0)
            {
                int bitIndex = BitOperations.TrailingZeroCount(word);
                indexes.Add((wordIndex << 6) + bitIndex);
                word &= word - 1;
            }
        }
        return indexes.ToArray();
    }
}
