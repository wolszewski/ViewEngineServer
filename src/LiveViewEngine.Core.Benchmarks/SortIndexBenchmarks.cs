using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using LiveViewEngine.Collections;

namespace LiveViewEngine.Core.Benchmarks;

// Compares OrderStatisticsTree (1-item-per-node LLRB) and NodeArrayTree (64-items-per-node WPF-style)
// against a sorted List<int> (the previous SortIndex backing structure).
// Run with: dotnet run -c Release --project src/LiveViewEngine.Core.Benchmarks
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "RatioSD")]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class SortIndexBenchmarks
{
    public readonly struct DefaultIntComparer : IComparer<int>
    {
        public int Compare(int x, int y) => x.CompareTo(y);
    }

    [Params(100, 1000, 10000, 100000)]
    public int N;

    private int[] _keys = [];
    private int[] _deleteKeys = [];

    // Pre-built structures for read/page benchmarks.
    private OrderStatisticsTree<DefaultIntComparer> _tree = null!;
    private NodeArrayTree<DefaultIntComparer> _naTree = null!;
    private List<int> _list = [];

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        _keys = Enumerable.Range(0, N).Select(_ => rng.Next()).Distinct().Take(N).ToArray();
        _deleteKeys = _keys.Take(N / 2).ToArray();

        _tree = BuildTree(_keys);
        _naTree = BuildNaTree(_keys);
        _list = BuildList(_keys);
    }

    // ── Insert ───────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Insert")]
    public List<int> List_Insert() => BuildList(_keys);

    [Benchmark]
    [BenchmarkCategory("Insert")]
    public OrderStatisticsTree<DefaultIntComparer> Tree_Insert() => BuildTree(_keys);

    [Benchmark]
    [BenchmarkCategory("Insert")]
    public NodeArrayTree<DefaultIntComparer> NATree_Insert() => BuildNaTree(_keys);

    // ── Delete ───────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Delete")]
    public void List_Delete()
    {
        var list = BuildList(_keys);
        foreach (var key in _deleteKeys)
        {
            int idx = list.BinarySearch(key);
            if (idx >= 0) { list.RemoveAt(idx); }
        }
    }

    [Benchmark]
    [BenchmarkCategory("Delete")]
    public void Tree_Delete()
    {
        var tree = BuildTree(_keys);
        foreach (var key in _deleteKeys) { tree.Delete(key); }
    }

    [Benchmark]
    [BenchmarkCategory("Delete")]
    public void NATree_Delete()
    {
        var tree = BuildNaTree(_keys);
        foreach (var key in _deleteKeys) { tree.Delete(key); }
    }

    // ── GetPage (unfiltered) ──────────────────────────────────────────────────

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("GetPage")]
    public int[] List_GetPage()
    {
        int startIndex = N / 3;
        int pageSize = Math.Min(50, N - startIndex);
        if (pageSize <= 0) { return []; }
        return _list.GetRange(startIndex, pageSize).ToArray();
    }

    [Benchmark]
    [BenchmarkCategory("GetPage")]
    public int[] Tree_GetPage()
    {
        int startIndex = N / 3;
        int pageSize = Math.Min(50, N - startIndex);
        if (pageSize <= 0) { return []; }
        var result = new int[pageSize];
        var cursor = _tree.GetCursor(startIndex);
        for (int i = 0; i < pageSize; i++) { cursor.MoveNext(); result[i] = cursor.Current; }
        return result;
    }

    [Benchmark]
    [BenchmarkCategory("GetPage")]
    public int[] NATree_GetPage()
    {
        int startIndex = N / 3;
        int pageSize = Math.Min(50, N - startIndex);
        if (pageSize <= 0) { return []; }
        var result = new int[pageSize];
        var cursor = _naTree.GetCursor(startIndex);
        for (int i = 0; i < pageSize; i++) { cursor.MoveNext(); result[i] = cursor.Current; }
        return result;
    }

    // ── Mixed workload ────────────────────────────────────────────────────────

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Mixed")]
    public void List_Mixed()
    {
        var list = BuildList(_keys);
        var rng = new Random(1);
        for (int i = 0; i < N / 4; i++)
        {
            int pos = rng.Next(list.Count);
            list.RemoveAt(pos);
            int newKey = rng.Next();
            int idx = ~list.BinarySearch(newKey);
            if (idx < 0) { idx = ~idx; }
            list.Insert(idx, newKey);
        }
        if (list.Count > 50) { _ = list.GetRange(0, 50); }
    }

    [Benchmark]
    [BenchmarkCategory("Mixed")]
    public void Tree_Mixed()
    {
        var tree = BuildTree(_keys);
        var rng = new Random(1);
        for (int i = 0; i < N / 4; i++)
        {
            int key = tree.GetByIndex(rng.Next(tree.Count));
            tree.Delete(key);
            tree.Insert(rng.Next());
        }
        if (tree.Count > 50)
        {
            var cursor = tree.GetCursor(0);
            for (int i = 0; i < 50; i++) { cursor.MoveNext(); }
        }
    }

    [Benchmark]
    [BenchmarkCategory("Mixed")]
    public void NATree_Mixed()
    {
        var tree = BuildNaTree(_keys);
        var rng = new Random(1);
        for (int i = 0; i < N / 4; i++)
        {
            int key = tree.GetByIndex(rng.Next(tree.Count));
            tree.Delete(key);
            tree.Insert(rng.Next());
        }
        if (tree.Count > 50)
        {
            var cursor = tree.GetCursor(0);
            for (int i = 0; i < 50; i++) { cursor.MoveNext(); }
        }
    }

    // ── IndexOf / global rank lookup ─────────────────────────────────────────────
    // Both structures support O(log n) rank lookup. List uses BinarySearch on a maintained
    // sorted array; tree uses subtree-size augmentation. Measures the cost of answering
    // "what is this key's 0-based position in sorted order?", not page membership.

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("IndexOf")]
    public int List_IndexOf()
    {
        int target = _list[N / 2];
        return _list.BinarySearch(target);
    }

    [Benchmark]
    [BenchmarkCategory("IndexOf")]
    public int Tree_IndexOf()
    {
        int target = _tree.GetByIndex(N / 2);
        return _tree.IndexOf(target);
    }

    [Benchmark]
    [BenchmarkCategory("IndexOf")]
    public int NATree_IndexOf()
    {
        int target = _naTree.GetByIndex(N / 2);
        return _naTree.IndexOf(target);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static List<int> BuildList(int[] keys)
    {
        var list = new List<int>(keys.Length);
        foreach (var key in keys)
        {
            int idx = list.BinarySearch(key);
            if (idx < 0) { list.Insert(~idx, key); }
        }
        return list;
    }

    private static OrderStatisticsTree<DefaultIntComparer> BuildTree(int[] keys)
    {
        var tree = new OrderStatisticsTree<DefaultIntComparer>(default);
        foreach (var key in keys) { tree.Insert(key); }
        return tree;
    }

    private static NodeArrayTree<DefaultIntComparer> BuildNaTree(int[] keys)
    {
        var tree = new NodeArrayTree<DefaultIntComparer>(default);
        foreach (var key in keys) { tree.Insert(key); }
        return tree;
    }
}

