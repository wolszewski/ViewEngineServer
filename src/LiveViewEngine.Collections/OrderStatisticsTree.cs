using System.Runtime.CompilerServices;

namespace LiveViewEngine.Collections;

// Left-Leaning Red-Black tree augmented with subtree sizes (order-statistics tree).
// Guarantees O(log n) for Insert, Delete, GetByIndex, and IndexOf.
// Based on Sedgewick's LLRB algorithm: https://sedgewick.io/wp-content/themes/sedgewick/papers/2008LLRB.pdf
//
// TComparer is specialised at JIT time: when it is a struct the comparison call is devirtualised
// and inlined, eliminating delegate-dispatch overhead on every tree operation.
public sealed class OrderStatisticsTree<TComparer> where TComparer : IComparer<int>
{
    private Node? _root;
    private TComparer _comparer;

    public OrderStatisticsTree(TComparer comparer)
    {
        _comparer = comparer;
    }

    public int Count { get; private set; }

    // Inserts key. Assumes key is not already present (use Contains first if unsure).
    public void Insert(int key)
    {
        _root = Insert(_root, key);
        _root.Red = false;
        Count++;
    }

    // Deletes key. Assumes key is present; behaviour is undefined if it is not.
    public void Delete(int key)
    {
        _root = Delete(_root!, key);
        if (_root != null) { _root.Red = false; }
        Count--;
    }

    // O(log n) search without rank accumulation — faster than IndexOf when rank is not needed.
    public bool Contains(int key)
    {
        var node = _root;
        while (node != null)
        {
            int cmp = _comparer.Compare(key, node.Key);
            if (cmp < 0) { node = node.Left; }
            else if (cmp > 0) { node = node.Right; }
            else { return true; }
        }
        return false;
    }

    // Returns the key at 0-based sort-order position. O(log n).
    public int GetByIndex(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        var h = _root!;
        while (true)
        {
            int leftSize = Size(h.Left);
            if (index < leftSize) { h = h.Left!; }
            else if (index == leftSize) { return h.Key; }
            else { index -= leftSize + 1; h = h.Right!; }
        }
    }

    // Returns the 0-based sort-order position of key, or -1 if not found. O(log n).
    public int IndexOf(int key)
    {
        var node = _root;
        int rank = 0;
        while (node != null)
        {
            int cmp = _comparer.Compare(key, node.Key);
            if (cmp < 0)
            {
                node = node.Left;
            }
            else if (cmp > 0)
            {
                rank += Size(node.Left) + 1;
                node = node.Right;
            }
            else
            {
                return rank + Size(node.Left);
            }
        }
        return -1;
    }

    // Returns a cursor positioned at startIndex (0-based). O(log n) to position.
    // If startIndex >= Count the cursor is immediately exhausted.
    public TreeCursor GetCursor(int startIndex)
    {
        var cursor = new TreeCursor();
        cursor.Initialize(_root, startIndex);
        return cursor;
    }

    // ── LLRB core ──────────────────────────────────────────────────────────────

    private Node Insert(Node? h, int key)
    {
        if (h == null) { return new Node(key); }

        int cmp = _comparer.Compare(key, h.Key);
        if (cmp < 0) { h.Left = Insert(h.Left, key); }
        else if (cmp > 0) { h.Right = Insert(h.Right, key); }

        if (IsRed(h.Right) && !IsRed(h.Left)) { h = RotateLeft(h); }
        if (IsRed(h.Left) && IsRed(h.Left!.Left)) { h = RotateRight(h); }
        if (IsRed(h.Left) && IsRed(h.Right)) { FlipColors(h); }

        h.Size = 1 + Size(h.Left) + Size(h.Right);
        return h;
    }

    private Node? Delete(Node h, int key)
    {
        if (_comparer.Compare(key, h.Key) < 0)
        {
            if (!IsRed(h.Left) && !IsRed(h.Left?.Left))
            {
                h = MoveRedLeft(h);
            }
            h.Left = Delete(h.Left!, key);
        }
        else
        {
            if (IsRed(h.Left)) { h = RotateRight(h); }
            if (_comparer.Compare(key, h.Key) == 0 && h.Right == null) { return null; }
            if (!IsRed(h.Right) && !IsRed(h.Right?.Left))
            {
                h = MoveRedRight(h);
            }
            if (_comparer.Compare(key, h.Key) == 0)
            {
                var min = Min(h.Right!);
                h.Key = min.Key;
                h.Right = DeleteMin(h.Right!);
            }
            else
            {
                h.Right = Delete(h.Right!, key);
            }
        }
        return Balance(h);
    }

    private static Node? DeleteMin(Node h)
    {
        if (h.Left == null) { return null; }
        if (!IsRed(h.Left) && !IsRed(h.Left.Left)) { h = MoveRedLeft(h); }
        h.Left = DeleteMin(h.Left!);
        return Balance(h);
    }

    private static Node Min(Node h)
    {
        while (h.Left != null) { h = h.Left; }
        return h;
    }

    private static Node MoveRedLeft(Node h)
    {
        FlipColors(h);
        if (IsRed(h.Right?.Left))
        {
            h.Right = RotateRight(h.Right!);
            h = RotateLeft(h);
            FlipColors(h);
        }
        return h;
    }

    private static Node MoveRedRight(Node h)
    {
        FlipColors(h);
        if (IsRed(h.Left?.Left))
        {
            h = RotateRight(h);
            FlipColors(h);
        }
        return h;
    }

    private static Node Balance(Node h)
    {
        if (IsRed(h.Right) && !IsRed(h.Left)) { h = RotateLeft(h); }
        if (IsRed(h.Left) && IsRed(h.Left!.Left)) { h = RotateRight(h); }
        if (IsRed(h.Left) && IsRed(h.Right)) { FlipColors(h); }
        h.Size = 1 + Size(h.Left) + Size(h.Right);
        return h;
    }

    private static Node RotateLeft(Node h)
    {
        var x = h.Right!;
        h.Right = x.Left;
        x.Left = h;
        x.Red = h.Red;
        h.Red = true;
        x.Size = h.Size;
        h.Size = 1 + Size(h.Left) + Size(h.Right);
        return x;
    }

    private static Node RotateRight(Node h)
    {
        var x = h.Left!;
        h.Left = x.Right;
        x.Right = h;
        x.Red = h.Red;
        h.Red = true;
        x.Size = h.Size;
        h.Size = 1 + Size(h.Left) + Size(h.Right);
        return x;
    }

    private static void FlipColors(Node h)
    {
        h.Red = !h.Red;
        h.Left!.Red = !h.Left.Red;
        h.Right!.Red = !h.Right.Red;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsRed(Node? n) => n != null && n.Red;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Size(Node? n) => n?.Size ?? 0;

    // ── Node ───────────────────────────────────────────────────────────────────

    internal sealed class Node
    {
        public int Key;
        public int Size = 1;
        public bool Red = true;
        public Node? Left, Right;

        public Node(int key) { Key = key; }
    }

    // ── Cursor ─────────────────────────────────────────────────────────────────

    // In-order cursor starting at a given 0-based position.
    // Initialize: O(log n). Each MoveNext: O(1) amortised.
    // Not thread-safe; do not mutate the tree while a cursor is live.
    public sealed class TreeCursor
    {
        // LLRB height ≤ 2·log₂(n). Stack of 64 is safe for n ≤ 2³¹.
        private readonly Node?[] _stack = new Node[64];
        private int _top;

        public int Current { get; private set; }

        internal void Initialize(Node? root, int startIndex)
        {
            _top = 0;
            var node = root;
            var remaining = startIndex;
            while (node != null)
            {
                int leftSize = Size(node.Left);
                if (remaining <= leftSize)
                {
                    _stack[_top++] = node;
                    if (remaining == leftSize) { break; } // this node IS the start
                    node = node.Left;
                }
                else
                {
                    remaining -= leftSize + 1; // this node is before our start window
                    node = node.Right;
                }
            }
        }

        public bool MoveNext()
        {
            if (_top == 0) { return false; }
            var node = _stack[--_top]!;
            Current = node.Key;
            // push the left spine of the right subtree so subsequent MoveNext()
            // calls yield the in-order successors without revisiting the whole tree
            var right = node.Right;
            while (right != null)
            {
                _stack[_top++] = right;
                right = right.Left;
            }
            return true;
        }
    }
}
