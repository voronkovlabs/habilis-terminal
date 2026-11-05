using System.Globalization;
using System.Text;
using System.Text.Json;
using HabilisTerminal.Core.Reporting;

namespace HabilisTerminal.Core.Diagnostics;

/// <summary>The local files of performance measuring (issue #130): one JSON line per period, removed once they have been sent.</summary>
public sealed class PerfStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new();

    public string Directory { get; } = directory;

    public long PendingBytes =>
        System.IO.Directory.Exists(Directory) ? new DirectoryInfo(Directory).EnumerateFiles("perf-*.jsonl").Sum(f => f.Length) : 0;

    public void Append(PerfPeriod period)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.AppendAllText(Path.Combine(Directory, $"perf-{Environment.ProcessId}.jsonl"), JsonSerializer.Serialize(period, Options) + Environment.NewLine);
    }

    /// <summary>All periods not sent yet, with the files they came from. A damaged line is skipped.</summary>
    public (IReadOnlyList<PerfPeriod> Periods, IReadOnlyList<string> Files) ReadPending()
    {
        var periods = new List<PerfPeriod>();
        var files = new List<string>();
        if (!System.IO.Directory.Exists(Directory))
        {
            return (periods, files);
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "perf-*.jsonl"))
        {
            files.Add(file);
            foreach (var line in File.ReadLines(file))
            {
                try
                {
                    if (JsonSerializer.Deserialize<PerfPeriod>(line, Options) is { } period)
                    {
                        periods.Add(period);
                    }
                }
                catch (JsonException)
                {
                    // Cut off by a crash: the rest still counts.
                }
            }
        }

        return (periods, files);
    }

    /// <summary>Sent data does not stay on the disk.</summary>
    public static void Delete(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            File.Delete(file);
        }
    }
}

/// <summary>Merges periods into the text of an issue: what took the time, and whether it computed or waited.</summary>
public static class PerfReport
{
    private const int MaxOperations = 80;
    private const int MaxStalls = 40;
    private const int MaxLongestStalls = 15;
    private const int MaxThreads = 25;
    private const double MinWallForKind = 50;

    public static IssueDraft Build(IReadOnlyList<PerfPeriod> periods, string version, string windows, int processors)
    {
        var culture = CultureInfo.InvariantCulture;
        var operations = Merge(periods);
        var stalls = periods.SelectMany(p => p.Stalls).OrderByDescending(s => s.WallMs).ToList();
        var measured = periods.Sum(p => (p.End - p.Start).TotalMinutes);
        var text = new StringBuilder();
        text.AppendLine(culture, $"Habilis Terminal {version} · {windows} · {processors} logical processors");
        text.AppendLine(culture, $"Measured {measured:0} min with activity. Process CPU {periods.Sum(p => p.ProcessCpuMs) / 1000:0.0} s, garbage collection pauses {periods.Sum(p => p.GcPauseMs) / 1000:0.0} s.");
        if (periods.Count > 0)
        {
            text.AppendLine(culture, $"Memory: working set up to {periods.Max(p => p.WorkingSetMb):0} MB, managed heap up to {periods.Max(p => p.ManagedHeapMb):0} MB.");
        }

        text.AppendLine();
        AppendThreads(text, periods);
        text.AppendLine("## UI thread stalls");
        text.AppendLine(culture, $"{stalls.Count} stalls of 100 ms or more, {stalls.Sum(s => s.WallMs) / 1000:0.0} s in all.");
        text.AppendLine();
        if (stalls.Count > 0)
        {
            text.AppendLine("| Under way | Count | Total ms | Longest ms | CPU share | Reads as |");
            text.AppendLine("|---|---:|---:|---:|---:|---|");
            foreach (var group in stalls.GroupBy(StallName).OrderByDescending(g => g.Sum(s => s.WallMs)).Take(MaxStalls))
            {
                var wall = group.Sum(s => s.WallMs);
                var cpu = group.Sum(s => s.CpuMs);
                text.AppendLine(culture, $"| {Cell(group.Key)} | {group.Count()} | {wall:0} | {group.Max(s => s.WallMs):0} | {Share(cpu, wall)} | {Kind(cpu, wall)} |");
            }

            text.AppendLine();
            text.AppendLine("Longest ones:");
            text.AppendLine();
            text.AppendLine("| At, s | ms | CPU share | Collections | Named operation | Dispatched |");
            text.AppendLine("|---:|---:|---:|---:|---|---|");
            var start = periods.Min(p => p.Start);
            foreach (var stall in stalls.Take(MaxLongestStalls))
            {
                text.AppendLine(culture, $"| {(stall.At - start).TotalSeconds:0} | {stall.WallMs:0} | {Share(stall.CpuMs, stall.WallMs)} | {stall.GarbageCollections} | {Cell(stall.Operation ?? "-")} | {Cell(stall.Dispatched ?? "-")} |");
            }

            text.AppendLine();
        }

        text.AppendLine("## Operations");
        text.AppendLine("By total time. CPU is the measuring thread's, counted by Windows in steps of about 16 ms, so it means little for small totals. None means the operation has awaits in it, so waiting and work cannot be told apart.");
        text.AppendLine();
        text.AppendLine("| Operation | Detail | Count | Total ms | Mean ms | p95 ms | Longest ms | CPU share | Reads as |");
        text.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---|");
        foreach (var operation in operations.OrderByDescending(o => o.WallMs).Take(MaxOperations))
        {
            var share = operation.CpuMs is { } cpu ? Share(cpu, operation.WallMs) : "-";
            var kind = operation.CpuMs is { } used ? Kind(used, operation.WallMs) : "asynchronous";
            text.AppendLine(culture, $"| {Cell(operation.Name)} | {Cell(operation.Detail ?? string.Empty)} | {operation.Count} | {operation.WallMs:0} | {operation.WallMs / Math.Max(1, operation.Count):0.0} | {Percentile95(operation):0} | {operation.MaxWallMs:0} | {share} | {kind} |");
        }

        return new IssueDraft($"Performance report {version}", text.ToString(), ["performance"]);
    }

    /// <summary>Where the process's CPU went, including what no measuring point covers.</summary>
    private static void AppendThreads(StringBuilder text, IReadOnlyList<PerfPeriod> periods)
    {
        var threads = periods.SelectMany(p => p.Threads ?? []).GroupBy(t => t.Name)
            .Select(g => (Name: g.Key, CpuMs: g.Sum(t => t.CpuMs))).Where(t => t.CpuMs >= 1).OrderByDescending(t => t.CpuMs).Take(MaxThreads).ToList();
        if (threads.Count == 0)
        {
            return;
        }

        var culture = CultureInfo.InvariantCulture;
        text.AppendLine("## CPU by thread");
        text.AppendLine("By the thread's name, or the module an unnamed thread started in.");
        text.AppendLine();
        text.AppendLine("| Thread | CPU, s |");
        text.AppendLine("|---|---:|");
        foreach (var (name, cpu) in threads)
        {
            text.AppendLine(culture, $"| {Cell(name)} | {cpu / 1000:0.0} |");
        }

        text.AppendLine();
    }

    /// <summary>Periods added up, by operation and detail.</summary>
    public static IReadOnlyList<PerfOperation> Merge(IEnumerable<PerfPeriod> periods) =>
        [.. periods.SelectMany(p => p.Operations).GroupBy(o => (o.Name, o.Detail)).Select(group =>
        {
            var buckets = new int[Perf.BucketCount];
            foreach (var operation in group)
            {
                for (var i = 0; i < buckets.Length && i < operation.Buckets.Length; i++)
                {
                    buckets[i] += operation.Buckets[i];
                }
            }

            var cpu = group.Where(o => o.CpuMs is not null).Select(o => o.CpuMs!.Value).ToList();
            return new PerfOperation(group.Key.Name, group.Key.Detail, group.Sum(o => o.Count), group.Sum(o => o.WallMs), cpu.Count > 0 ? cpu.Sum() : null, group.Max(o => o.MaxWallMs), buckets);
        })];

    /// <summary>The upper limit of the bucket holding the 95th percentile; never above the longest seen.</summary>
    public static double Percentile95(PerfOperation operation)
    {
        var target = Math.Ceiling(operation.Count * 0.95);
        long seen = 0;
        for (var i = 0; i < operation.Buckets.Length; i++)
        {
            seen += operation.Buckets[i];
            if (seen >= target)
            {
                return Math.Min(Math.Pow(2, i), operation.MaxWallMs);
            }
        }

        return operation.MaxWallMs;
    }

    /// <summary>
    /// Mostly CPU: optimise it. Mostly not: it waits, so make it asynchronous. Windows counts thread CPU in steps of about
    /// 16 ms, so a small total says nothing either way.
    /// </summary>
    public static string Kind(double cpuMs, double wallMs)
    {
        if (wallMs < MinWallForKind)
        {
            return "too short to tell";
        }

        var share = cpuMs / wallMs;
        return share >= 0.7 ? "computes" : share <= 0.3 ? "waits" : "mixed";
    }

    private static string StallName(PerfStall stall) =>
        stall.Operation ?? (stall.Dispatched is { Length: > 0 } dispatched ? $"dispatched: {dispatched}" : "window messages, nothing named");

    private static string Share(double cpuMs, double wallMs) =>
        wallMs > 0 ? string.Create(CultureInfo.InvariantCulture, $"{Math.Min(1, cpuMs / wallMs) * 100:0}%") : "-";

    private static string Cell(string text) => text.Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ');
}
