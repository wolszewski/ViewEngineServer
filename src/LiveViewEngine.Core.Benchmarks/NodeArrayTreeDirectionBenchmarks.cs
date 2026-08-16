using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using LiveViewEngine.Collections;

namespace LiveViewEngine.Core.Benchmarks;

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "RatioSD")]
public class NodeArrayTreeDirectionBenchmarks
{
    public readonly struct DefaultIntComparer : IComparer<int>
    {
        public int Compare(int x, int y) => x.CompareTo(y);
    }

    [Params(1000, 10000, 100000)]
    public int N;

    private NodeArrayTree<DefaultIntComparer> _tree = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tree = new NodeArrayTree<DefaultIntComparer>(default);
        foreach (var key in Enumerable.Range(0, N))
        {
            _tree.Insert(key);
        }
    }

    [Benchmark(Baseline = true)]
    public int[] ForwardPage()
    {
        int startIndex = N / 3;
        int pageSize = Math.Min(50, N - startIndex);
        if (pageSize <= 0)
        {
            return [];
        }

        var result = new int[pageSize];
        _tree.Take(startIndex, result);
        return result;
    }

    [Benchmark]
    public int[] ReversePage()
    {
        int startIndex = N / 3;
        int pageSize = Math.Min(50, N - startIndex);
        if (pageSize <= 0)
        {
            return [];
        }

        var result = new int[pageSize];
        _tree.TakeReverse(_tree.Count - 1 - startIndex, result);
        return result;
    }
}
