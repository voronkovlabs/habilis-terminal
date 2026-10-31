using System.Globalization;
using BenchmarkDotNet.Attributes;
using HabilisTerminal.Core.FileSystem;

namespace HabilisTerminal.Core.Benchmarks;

/// <summary>SPEC §11 targets on a 100k-entry directory: first batch ≤ 150 ms, read + sort ≤ 500 ms.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 8)]
public class DirectoryBenchmarks
{
    private LargeDirectory _directory = null!;
    private FileEntry[] _entries = [];

    [Params(100_000)]
    public int FileCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directory = new LargeDirectory(FileCount);
        _entries = DirectoryReader.ReadAll(_directory.Path);
    }

    [Benchmark]
    public int FirstBatch()
    {
        foreach (var batch in DirectoryReader.ReadBatches(_directory.Path))
        {
            return batch.Length;
        }

        return 0;
    }

    [Benchmark]
    public int ReadAll() => DirectoryReader.ReadAll(_directory.Path).Length;

    [Benchmark]
    public int SortByName() => EntrySorter.Sort(_entries, new SortOptions(), CultureInfo.CurrentCulture).Length;

    [Benchmark]
    public int SortByExtension() => EntrySorter.Sort(_entries, new SortOptions(SortMode.Extension), CultureInfo.CurrentCulture).Length;

    [Benchmark]
    public int SortBySize() => EntrySorter.Sort(_entries, new SortOptions(SortMode.Size), CultureInfo.CurrentCulture).Length;

    [Benchmark(Description = "ReadAll + SortByName")]
    public int ReadAndSort() => EntrySorter.Sort(DirectoryReader.ReadAll(_directory.Path), new SortOptions(), CultureInfo.CurrentCulture).Length;

    [Benchmark]
    public int SelectByMask()
    {
        var selection = new Selection();
        return selection.SelectByMask(_entries, FileMask.Parse("*.cs,*.json"));
    }
}
