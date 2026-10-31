using BenchmarkDotNet.Running;
using HabilisTerminal.Core.Benchmarks;
using HabilisTerminal.Core.FileSystem;

// --clean   removes the generated directories under %TEMP%\habi-bench
// --memory  measures memory retained by one 100k listing
// otherwise BenchmarkDotNet arguments (e.g. --filter *)
if (args.Contains("--clean"))
{
    LargeDirectory.Clean();
    return;
}

if (args.Contains("--memory"))
{
    var directory = new LargeDirectory(100_000);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var before = GC.GetTotalMemory(forceFullCollection: true);
    var entries = DirectoryReader.ReadAll(directory.Path);
    var after = GC.GetTotalMemory(forceFullCollection: true);
    Console.WriteLine($"Entries: {entries.Length:N0}; retained: {(after - before) / 1024.0 / 1024.0:F1} MB");
    GC.KeepAlive(entries);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(DirectoryBenchmarks).Assembly).Run(args);
