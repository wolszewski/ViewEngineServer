using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using LiveViewEngine.Core;

namespace LiveViewEngine.Core.Benchmarks;

// Compares OrderStatisticsTree against a sorted List<int> (the previous SortIndex backing structure).
// Run with: dotnet run -c Release --project src/LiveViewEngine.Core.Benchmarks0
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "RatioSD")]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class SortIndexBenchmarks
{
    [Params(100, 1_000, 10_000)]
    public int N;

    private int[] _keys = [];
    private int[] _deleteKeys = [];

    // Pre-built structures for read/page benchmarks.
    private OrderStatisticsTree _tree = null!;
    private List<int> _list = [];

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        _keys = Enumerable.Range(0, N).Select(_ => rng.Next()).Distinct().Take(N).ToArray();
        _deleteKeys = _keys.Take(N / 2).ToArray();

        // Build a pre-populated tree and list for read benchmarks.
        _tree = BuildTree(_keys);
        _list = BuildList(_keys);
    }

    // ── Insert ───────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Insert")]
    public List<int> List_Insert()
    {
        return BuildList(_keys);
    }

    [Benchmark]
    [BenchmarkCategory("Insert")]
    public OrderStatisticsTree Tree_Insert()
    {
        return BuildTree(_keys);
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Delete")]
    public void List_Delete()
    {
        var list = BuildList(_keys);
        foreach (var key in _deleteKeys)
        {
            int idx = ListBinarySearch(list, key);
            if (idx >= 0) { list.RemoveAt(idx); }
        }
    }

    [Benchmark]
    [BenchmarkCategory("Delete")]
    public void Tree_Delete()
    {
        var tree = BuildTree(_keys);
        foreach (var key in _deleteKeys)
        {
            tree.Delete(key);
        }
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
        for (int i = 0; i < pageSize; i++)
        {
            cursor.MoveNext();
            result[i] = cursor.Current;
        }
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
            // delete a random element
            int pos = rng.Next(list.Count);
            list.RemoveAt(pos);
            // insert a new one
            int newKey = rng.Next();
            int idx = ~list.BinarySearch(newKey);
            if (idx < 0) { idx = ~idx; }
            list.Insert(idx, newKey);
        }
        // page read
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
            // delete element at random position
            int pos = rng.Next(tree.Count);
            int key = tree.GetByIndex(pos);
            tree.Delete(key);
            // insert a new one
            tree.Insert(rng.Next());
        }
        // page read
        if (tree.Count > 50)
        {
            var cursor = tree.GetCursor(0);
            for (int i = 0; i < 50; i++) { cursor.MoveNext(); }
        }
    }

    // ── IndexOf / position lookup ─────────────────────────────────────────────

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("IndexOf")]
    public int List_IndexOf()
    {
        // simulate checking if a row is on the current page
        int target = _list[N / 2];
        return ListBinarySearch(_list, target);
    }

    [Benchmark]
    [BenchmarkCategory("IndexOf")]
    public int Tree_IndexOf()
    {
        int target = _tree.GetByIndex(N / 2);
        return _tree.IndexOf(target);
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

    private static OrderStatisticsTree BuildTree(int[] keys)
    {
        var tree = new OrderStatisticsTree(Comparer<int>.Default.Compare);
        foreach (var key in keys) { tree.Insert(key); }
        return tree;
    }

    private static int ListBinarySearch(List<int> list, int key)
    {
        return list.BinarySearch(key);
    }
}
