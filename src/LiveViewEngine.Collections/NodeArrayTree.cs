using System.Runtime.CompilerServices;

namespace LiveViewEngine.Collections;

// Adapted from WPF PresentationFramework RBTree<T>/RBNode<T>/RBFinger<T>.
// Original: Copyright (c) .NET Foundation, MIT License.
// Modifications: stripped INotifyPropertyChanged, IList<T>, Sort/InsertionSort, WPF deps;
// added generic TComparer struct parameter; restricted to int keys.
//
// Each tree node holds up to MaxSize=64 sorted items in a contiguous int[].
// Fewer allocations (~16x) and better sequential-scan cache locality vs 1-item-per-node trees.
public sealed class NodeArrayTree<TComparer> where TComparer : IComparer<int>
{
    private const int MaxSize = 64;
    private const int BinarySearchThreshold = 3;

    private readonly Node _sentinel;
    private readonly TComparer _comparer;

    public NodeArrayTree(TComparer comparer)
    {
        _comparer = comparer;
        _sentinel = new Node(isSentinel: true)
        {
            Owner = this,
            Size = MaxSize,
        };
    }

    public int Count => _sentinel.LeftSize;

    public int Insert(int key)
    {
        var finger = Find(key);
        var node = finger.Node;

        if (node.IsSentinel)
        {
            node = InsertNode(0);
            node.InsertAt(0, key);
        }
        else if (node.Size < MaxSize)
        {
            node.InsertAt(finger.Offset, key);
        }
        else
        {
            var successor = node.GetSuccessor();
            Node? succsucc = null;
            if (successor.Size >= MaxSize)
            {
                if (!successor.IsSentinel)
                {
                    succsucc = successor;
                }

                successor = InsertNode(finger.Index + node.Size - finger.Offset);
            }

            node.InsertAt(finger.Offset, key, successor, succsucc);
        }

        _sentinel.LeftChild?.IsRed = false;
        return finger.Index;
    }

    public int Delete(int key)
    {
        var finger = Find(key);
        if (!finger.Found)
        {
            throw new KeyNotFoundException($"Key '{key}' was not found.");
        }

        int position = finger.Index;
        finger.Node.RemoveAt(ref finger);
        _sentinel.LeftChild?.IsRed = false;
        return position;
    }

    public int TryDelete(int key)
    {
        var finger = Find(key);
        if (!finger.Found)
        {
            return -1;
        }

        int position = finger.Index;
        finger.Node.RemoveAt(ref finger);
        _sentinel.LeftChild?.IsRed = false;
        return position;
    }

    public bool Contains(int key)
    {
        return Find(key).Found;
    }

    public int GetByIndex(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var finger = _sentinel.LeftChild!.FindIndex(index);
        return finger.Node.Data[finger.Offset];
    }

    public int IndexOf(int key)
    {
        var finger = Find(key);
        return finger.Found ? finger.Index : -1;
    }

    public TreeCursor GetCursor(int startIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);

        if (startIndex >= Count || _sentinel.LeftChild == null)
        {
            return new TreeCursor(_sentinel, 0);
        }

        var finger = _sentinel.LeftChild.FindIndex(startIndex);
        return new TreeCursor(finger.Node, finger.Offset);
    }

    private Node InsertNode(int index)
    {
        _sentinel.LeftChild = Node.InsertNode(this, _sentinel, _sentinel.LeftChild, index, out var newNode);
        _sentinel.LeftChild!.Parent = _sentinel;
        return newNode;
    }

    private void RemoveNode(int index)
    {
        if (_sentinel.LeftChild == null)
        {
            return;
        }

        _sentinel.LeftChild = Node.DeleteNode(_sentinel, _sentinel.LeftChild, index);
        if (_sentinel.LeftChild != null)
        {
            _sentinel.LeftChild.Parent = _sentinel;
            _sentinel.LeftChild.IsRed = false;
        }
    }

    private Finger Find(int key)
    {
        if (_sentinel.LeftChild == null)
        {
            return new Finger
            {
                Node = _sentinel,
                Offset = 0,
                Index = 0,
                Found = false,
            };
        }

        return _sentinel.LeftChild.Find(key, _comparer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsRed(Node? node)
    {
        return node != null && node.IsRed;
    }

    internal struct Finger
    {
        public Node Node;
        public int Offset;
        public int Index;
        public bool Found;

        public static Finger operator ++(Finger finger)
        {
            finger.Offset += 1;
            finger.Index += 1;
            if (finger.Offset == finger.Node.Size)
            {
                finger.Node = finger.Node.GetSuccessor();
                finger.Offset = 0;
            }

            return finger;
        }
    }

    internal sealed class Node
    {
        public readonly int[] Data;

        public int Size;
        public int LeftSize;
        public bool IsRed;
        public Node? LeftChild;
        public Node? RightChild;
        public Node? Parent;
        public readonly bool IsSentinel;
        public NodeArrayTree<TComparer>? Owner;

        public Node(bool isSentinel = false)
        {
            Data = new int[MaxSize];
            IsSentinel = isSentinel;
        }

        public Node GetSuccessor()
        {
            Node node;
            Node? parent;

            if (RightChild == null)
            {
                for (node = this, parent = node.Parent; parent != null && parent.RightChild == node; node = parent, parent = node.Parent)
                {
                }

                return parent!;
            }

            for (parent = RightChild, node = parent.LeftChild!; node != null; parent = node, node = parent.LeftChild!)
            {
            }

            return parent;
        }

        public Node? GetPredecessor()
        {
            Node node;
            Node? parent;

            if (LeftChild == null)
            {
                for (node = this, parent = node.Parent; parent != null && parent.LeftChild == node; node = parent, parent = node.Parent)
                {
                }

                return parent;
            }

            for (parent = LeftChild, node = parent.RightChild!; node != null; parent = node, node = parent.RightChild!)
            {
            }

            return parent;
        }

        public Finger FindIndex(int index, bool exists = true)
        {
            Finger result;
            int delta = exists ? 1 : 0;

            if (index + delta <= LeftSize)
            {
                if (LeftChild == null)
                {
                    result = new Finger { Node = this, Offset = 0, Index = 0, Found = false };
                }
                else
                {
                    result = LeftChild.FindIndex(index, exists);
                }
            }
            else if (index < LeftSize + Size)
            {
                result = new Finger { Node = this, Offset = index - LeftSize, Index = index, Found = true };
            }
            else
            {
                if (RightChild == null)
                {
                    result = new Finger
                    {
                        Node = this,
                        Offset = Size,
                        Index = LeftSize + Size,
                        Found = false,
                    };
                }
                else
                {
                    result = RightChild.FindIndex(index - LeftSize - Size, exists);
                    result.Index += LeftSize + Size;
                }
            }

            return result;
        }

        public Finger Find(int x, TComparer comparer)
        {
            Finger result;
            int compL = Compare(comparer, x, Data[0]);

            if (compL <= 0)
            {
                if (LeftChild == null)
                {
                    result = new Finger { Node = this, Offset = 0, Index = 0, Found = compL == 0 };
                }
                else
                {
                    result = LeftChild.Find(x, comparer);
                    if (compL == 0 && !result.Found)
                    {
                        result = new Finger { Node = this, Offset = 0, Index = LeftSize, Found = true };
                    }
                }
            }
            else
            {
                int compR = Compare(comparer, x, Data[Size - 1]);
                if (compR <= 0)
                {
                    int offset = BinarySearch(x, 1, Size - 1, comparer, compR, out bool found);
                    result = new Finger { Node = this, Offset = offset, Index = LeftSize + offset, Found = found };
                }
                else if (RightChild == null)
                {
                    result = new Finger { Node = this, Offset = Size, Index = LeftSize + Size, Found = false };
                }
                else
                {
                    result = RightChild.Find(x, comparer);
                    result.Index += LeftSize + Size;
                }
            }

            return result;
        }

        public int BinarySearch(int x, int low, int high, TComparer comparer, int compHigh, out bool found)
        {
            while (high - low > BinarySearchThreshold)
            {
                int mid = (high + low) / 2;
                int comp = Compare(comparer, x, Data[mid]);
                if (comp <= 0)
                {
                    compHigh = comp;
                    high = mid;
                }
                else
                {
                    low = mid + 1;
                }
            }

            int finalComp = 0;
            for (; low < high; ++low)
            {
                finalComp = Compare(comparer, x, Data[low]);
                if (finalComp <= 0)
                {
                    break;
                }
            }

            if (low == high)
            {
                finalComp = compHigh;
            }

            found = finalComp == 0;
            return low;
        }

        public void InsertAt(int offset, int x, Node? successor = null, Node? succsucc = null)
        {
            if (Size < MaxSize)
            {
                Array.Copy(Data, offset, Data, offset + 1, Size - offset);
                Data[offset] = x;
                ChangeSize(1);
                return;
            }

            if (successor == null)
            {
                throw new InvalidOperationException("A successor node is required when inserting into a full node.");
            }

            if (successor.Size == 0)
            {
                if (succsucc == null)
                {
                    if (offset < MaxSize)
                    {
                        successor.InsertAt(0, Data[MaxSize - 1]);
                        Array.Copy(Data, offset, Data, offset + 1, MaxSize - offset - 1);
                        Data[offset] = x;
                    }
                    else
                    {
                        successor.InsertAt(0, x);
                    }
                }
                else
                {
                    int s = MaxSize / 3;

                    Array.Copy(successor.Data, 0, successor.Data, s, successor.Size);
                    Array.Copy(Data, MaxSize - s, successor.Data, 0, s);

                    Array.Copy(succsucc.Data, 0, successor.Data, s + successor.Size, s);
                    Array.Copy(succsucc.Data, s, succsucc.Data, 0, MaxSize - s);

                    if (offset <= MaxSize - s)
                    {
                        Array.Copy(Data, offset, Data, offset + 1, MaxSize - s - offset);
                        Data[offset] = x;

                        ChangeSize(1 - s);
                        successor.ChangeSize(s + s);
                    }
                    else
                    {
                        int successorOffset = offset - (MaxSize - s);
                        Array.Copy(
                            successor.Data,
                            successorOffset,
                            successor.Data,
                            successorOffset + 1,
                            successor.Size + s + s - successorOffset);
                        successor.Data[successorOffset] = x;

                        ChangeSize(-s);
                        successor.ChangeSize(s + s + 1);
                    }

                    succsucc.ChangeSize(-s);
                }

                return;
            }

            int split = (Size + successor.Size + 1) / 2;

            if (offset < split)
            {
                Array.Copy(successor.Data, 0, successor.Data, MaxSize - split + 1, successor.Size);
                Array.Copy(Data, split - 1, successor.Data, 0, MaxSize - split + 1);

                Array.Copy(Data, offset, Data, offset + 1, split - 1 - offset);
                Data[offset] = x;
            }
            else
            {
                Array.Copy(successor.Data, 0, successor.Data, MaxSize - split, successor.Size);
                Array.Copy(Data, split, successor.Data, 0, MaxSize - split);

                Array.Copy(successor.Data, offset - split, successor.Data, offset - split + 1, successor.Size + MaxSize - offset);
                successor.Data[offset - split] = x;
            }

            ChangeSize(split - MaxSize);
            successor.ChangeSize(MaxSize - split + 1);
        }

        public void RemoveAt(ref Finger finger)
        {
            Node node = finger.Node;
            int offset = finger.Offset;

            Array.Copy(node.Data, offset + 1, node.Data, offset, node.Size - offset - 1);
            node.ChangeSize(-1);

            if (node.Size == 0)
            {
                finger.Node = node.GetSuccessor();
                finger.Offset = 0;

                NodeArrayTree<TComparer> root = GetRootAndIndex(node, out int index);
                root.RemoveNode(index);
            }

            finger.Offset -= 1;
        }

        public void ChangeSize(int delta)
        {
            if (delta == 0)
            {
                return;
            }

            if (delta < 0)
            {
                for (int k = Size + delta; k < Size; ++k)
                {
                    Data[k] = 0;
                }
            }

            Size += delta;
            for (Node node = this; node.Parent != null; node = node.Parent)
            {
                if (node.Parent.LeftChild == node)
                {
                    node.Parent.LeftSize += delta;
                }
            }
        }

        public Node InsertNodeAfter(Node node)
        {
            NodeArrayTree<TComparer> root = GetRootAndIndex(node, out int index);
            return root.InsertNode(index + node.Size);
        }

        public static NodeArrayTree<TComparer> GetRootAndIndex(Node node, out int index)
        {
            index = node.LeftSize;
            for (Node? parent = node.Parent; parent != null; node = parent, parent = node.Parent)
            {
                if (node == parent.RightChild)
                {
                    index += parent.LeftSize + parent.Size;
                }
            }

            return node.Owner!;
        }

        public static Node Substitute(Node node, Node sub, Node parent)
        {
            sub.LeftChild = node.LeftChild;
            sub.RightChild = node.RightChild;
            sub.LeftSize = node.LeftSize;
            sub.Parent = node.Parent;
            sub.IsRed = node.IsRed;

            if (sub.LeftChild != null)
            {
                sub.LeftChild.Parent = sub;
            }

            if (sub.RightChild != null)
            {
                sub.RightChild.Parent = sub;
            }

            return sub;
        }

        public static Node InsertNode(NodeArrayTree<TComparer> root, Node parent, Node? node, int index, out Node newNode)
        {
            if (node == null)
            {
                newNode = new Node
                {
                    Parent = parent,
                    IsRed = true,
                };

                return newNode;
            }

            if (index <= node.LeftSize)
            {
                node.LeftChild = InsertNode(root, node, node.LeftChild, index, out newNode);
                if (node.LeftChild != null)
                {
                    node.LeftChild.Parent = node;
                }
            }
            else
            {
                node.RightChild = InsertNode(root, node, node.RightChild, index - node.LeftSize - node.Size, out newNode);
                if (node.RightChild != null)
                {
                    node.RightChild.Parent = node;
                }
            }

            return Fixup(node);
        }

        public static Node? DeleteNode(Node parent, Node node, int index)
        {
            if (index < node.LeftSize || (index == node.LeftSize && node.Size > 0))
            {
                if (!IsRed(node.LeftChild) && !IsRed(node.LeftChild?.LeftChild))
                {
                    node = node.MoveRedLeft();
                }

                node.LeftChild = DeleteNode(node, node.LeftChild!, index);
                if (node.LeftChild != null)
                {
                    node.LeftChild.Parent = node;
                }
            }
            else
            {
                bool deleteHere = index == node.LeftSize;

                if (IsRed(node.LeftChild))
                {
                    node = node.RotateRight();
                    deleteHere = false;
                }

                if (deleteHere && node.RightChild == null)
                {
                    return null;
                }

                if (!IsRed(node.RightChild) && !IsRed(node.RightChild?.LeftChild))
                {
                    Node original = node;
                    node = node.MoveRedRight();
                    deleteHere = deleteHere && ReferenceEquals(original, node);
                }

                if (deleteHere)
                {
                    node.RightChild = DeleteLeftmost(node.RightChild!, out Node sub);
                    if (node.RightChild != null)
                    {
                        node.RightChild.Parent = node;
                    }

                    node = Substitute(node, sub, parent);
                }
                else
                {
                    node.RightChild = DeleteNode(node, node.RightChild!, index - node.LeftSize - node.Size);
                    if (node.RightChild != null)
                    {
                        node.RightChild.Parent = node;
                    }
                }
            }

            return Fixup(node);
        }

        public static Node? DeleteLeftmost(Node node, out Node leftmost)
        {
            if (node.LeftChild == null)
            {
                leftmost = node;
                return null;
            }

            if (!IsRed(node.LeftChild) && !IsRed(node.LeftChild.LeftChild))
            {
                node = node.MoveRedLeft();
            }

            node.LeftChild = DeleteLeftmost(node.LeftChild!, out leftmost);
            if (node.LeftChild != null)
            {
                node.LeftChild.Parent = node;
            }

            node.LeftSize -= leftmost.Size;
            return Fixup(node);
        }

        public static Node Fixup(Node node)
        {
            if (!IsRed(node.LeftChild) && IsRed(node.RightChild))
            {
                node = node.RotateLeft();
            }

            if (IsRed(node.LeftChild) && IsRed(node.LeftChild!.LeftChild))
            {
                node = node.RotateRight();
            }

            if (IsRed(node.LeftChild) && IsRed(node.RightChild))
            {
                node.ColorFlip();
            }

            return node;
        }

        public Node RotateLeft()
        {
            Node node = RightChild!;
            node.LeftSize += LeftSize + Size;
            node.IsRed = IsRed;
            node.Parent = Parent;
            RightChild = node.LeftChild;
            if (RightChild != null)
            {
                RightChild.Parent = this;
            }

            node.LeftChild = this;
            IsRed = true;
            Parent = node;
            return node;
        }

        public Node RotateRight()
        {
            Node node = LeftChild!;
            LeftSize -= node.LeftSize + node.Size;
            node.IsRed = IsRed;
            node.Parent = Parent;
            LeftChild = node.RightChild;
            if (LeftChild != null)
            {
                LeftChild.Parent = this;
            }

            node.RightChild = this;
            IsRed = true;
            Parent = node;
            return node;
        }

        public void ColorFlip()
        {
            IsRed = !IsRed;
            LeftChild!.IsRed = !LeftChild.IsRed;
            RightChild!.IsRed = !RightChild.IsRed;
        }

        public Node MoveRedLeft()
        {
            ColorFlip();
            if (IsRed(RightChild?.LeftChild))
            {
                RightChild = RightChild!.RotateRight();
                RightChild.Parent = this;
                Node node = RotateLeft();
                node.ColorFlip();
                return node;
            }

            return this;
        }

        public Node MoveRedRight()
        {
            ColorFlip();
            if (IsRed(LeftChild?.LeftChild))
            {
                Node node = RotateRight();
                node.ColorFlip();
                return node;
            }

            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Compare(TComparer comparer, int x, int y)
        {
            return comparer.Compare(x, y);
        }
    }

    public sealed class TreeCursor
    {
        private Node _node;
        private int _offset;

        internal TreeCursor(Node node, int offset)
        {
            _node = node;
            _offset = offset;
        }

        public int Current { get; private set; }

        public bool MoveNext()
        {
            if (_node.IsSentinel)
            {
                return false;
            }

            Current = _node.Data[_offset];
            _offset += 1;
            if (_offset == _node.Size)
            {
                _node = _node.GetSuccessor();
                _offset = 0;
            }

            return true;
        }
    }
}
