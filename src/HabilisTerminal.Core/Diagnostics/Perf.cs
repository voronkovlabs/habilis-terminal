using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HabilisTerminal.Core.Diagnostics;

/// <summary>One kind of operation over a period. CpuMs is that of the measuring thread, only for operations that stay on it.</summary>
/// <param name="Buckets">Counts by wall time: bucket n holds up to 2^n ms, the last everything longer.</param>
public sealed record PerfOperation(string Name, string? Detail, long Count, double WallMs, double? CpuMs, double MaxWallMs, int[] Buckets);

/// <summary>The UI thread did not answer for a while. CpuMs tells computing from waiting; Operation is what was under way.</summary>
/// <param name="ThreadState">What Windows said the UI thread was doing, most often seen during the stall: "running", "ready" or "wait: reason".</param>
/// <param name="WatchdogLateMs">How late the measuring thread woke at worst: a large value means the whole machine stood, not only the UI thread.</param>
/// <param name="MemoryLoad">Percent of the machine's physical memory in use when the stall ended.</param>
public sealed record PerfStall(DateTime At, double WallMs, double CpuMs, int GarbageCollections, string? Operation, string? Dispatched, string? ThreadState = null, double? WatchdogLateMs = null, int? MemoryLoad = null)
{
    /// <summary>All states seen, with shares: "wait: Executive 70%, ready 30%".</summary>
    public string? ThreadStates { get; init; }

    /// <summary>The kind of object waited for and who holds it, as far as Windows' wait chains can tell.</summary>
    public string? WaitChain { get; init; }

    /// <summary>The native stack, innermost first: module and function names only.</summary>
    public string? Stack { get; init; }

    /// <summary>Percent of the machine's processors busy during the stall.</summary>
    public int? MachineCpu { get; init; }

    public double? DiskQueue { get; init; }

    /// <summary>The mean time of one disk transfer during the stall.</summary>
    public double? DiskTransferMs { get; init; }
}

/// <summary>The stack of the busiest thread when sampled, with the CPU it used up to then: where unexplained CPU goes.</summary>
public sealed record PerfBusyThread(string Thread, string Stack, double CpuMs);

/// <summary>CPU of the threads with one name over a period: a thread's description, or the module it started in.</summary>
public sealed record PerfThread(string Name, double CpuMs);

/// <summary>What one period collected: a line of the local files.</summary>
public sealed record PerfPeriod(DateTime Start, DateTime End, IReadOnlyList<PerfOperation> Operations, IReadOnlyList<PerfStall> Stalls, double ProcessCpuMs, double GcPauseMs)
{
    public IReadOnlyList<PerfThread>? Threads { get; init; }

    public double WorkingSetMb { get; init; }

    public double ManagedHeapMb { get; init; }

    /// <summary>Percent of the machine's processors busy over the period: on a loaded machine wall-clock times mean less.</summary>
    public int? MachineCpu { get; init; }

    public IReadOnlyList<PerfBusyThread>? BusyStacks { get; init; }
}

/// <summary>
/// Optional performance measuring (issue #130), off by default, when it costs one check per call. Names and details are
/// impersonal by rule: operation names, drive types, extensions, plugin ids; never paths, file names or typed text,
/// since reports go to public issues.
/// </summary>
public static class Perf
{
    public const int BucketCount = 16;

    private static readonly ConcurrentDictionary<(string Name, string? Detail), Counter> Counters = new();
    private static readonly List<PerfStall> Stalls = [];
    private static DateTime _periodStart = DateTime.UtcNow;
    private static TimeSpan _cpuAtStart;
    private static TimeSpan _gcAtStart;
    private static int _uiThreadId = -1;
    private static volatile string? _uiOperation;

    public static bool Enabled { get; private set; }

    /// <summary>Something was measured since the last period, so a period will be kept.</summary>
    public static bool HasActivity
    {
        get
        {
            lock (Stalls)
            {
                return !Counters.IsEmpty || Stalls.Count > 0;
            }
        }
    }

    /// <summary>The named operation under way on the UI thread, read by the watchdog.</summary>
    public static string? UiOperation => _uiOperation;

    public static void Start(int uiThreadId)
    {
        _uiThreadId = uiThreadId;
        Reset();
        Enabled = true;
    }

    public static void Stop() => Enabled = false;

    /// <summary>Work that stays on this thread: wall time and this thread's CPU.</summary>
    public static Scope Measure(string name, string? detail = null) => Enabled ? new Scope(name, detail, withCpu: true) : default;

    /// <summary>Work with awaits in it: wall time only, as the thread does other things meanwhile.</summary>
    public static Scope MeasureAsync(string name, string? detail = null) => Enabled ? new Scope(name, detail, withCpu: false) : default;

    /// <summary>A point in time since process start, such as the first window shown.</summary>
    public static void RecordMilestone(string name, double wallMs, string? detail = null) => Add(name, detail, wallMs, null);

    /// <summary>Impersonal context for a measurement: the kind of drive, never the path.</summary>
    public static string DriveKind(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return "network";
        }

        try
        {
            return new DriveInfo(path).DriveType.ToString().ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return "other";
        }
    }

    /// <summary>A number as a range, so that a report shows the scale and not the exact content of a folder.</summary>
    public static string CountRange(int count) => count switch
    {
        < 100 => "under 100",
        < 1000 => "100-1k",
        < 10000 => "1k-10k",
        < 100000 => "10k-100k",
        _ => "over 100k",
    };

    public static void RecordStall(PerfStall stall)
    {
        lock (Stalls)
        {
            Stalls.Add(stall);
        }
    }

    /// <summary>What was collected since the last call, or null when nothing was.</summary>
    public static PerfPeriod? TakePeriod(IReadOnlyList<PerfThread>? threads = null)
    {
        var operations = new List<PerfOperation>();
        foreach (var (key, counter) in Counters)
        {
            if (Counters.TryRemove(key, out _))
            {
                operations.Add(counter.ToOperation(key.Name, key.Detail));
            }
        }

        List<PerfStall> stalls;
        lock (Stalls)
        {
            stalls = [.. Stalls];
            Stalls.Clear();
        }

        if (operations.Count == 0 && stalls.Count == 0)
        {
            return null;
        }

        var cpu = Environment.CpuUsage.TotalTime;
        var gc = GC.GetTotalPauseDuration();
        var period = new PerfPeriod(_periodStart, DateTime.UtcNow, operations, stalls, (cpu - _cpuAtStart).TotalMilliseconds, (gc - _gcAtStart).TotalMilliseconds)
        {
            Threads = threads,
            WorkingSetMb = Environment.WorkingSet / 1048576.0,
            ManagedHeapMb = GC.GetTotalMemory(false) / 1048576.0,
        };
        _periodStart = period.End;
        _cpuAtStart = cpu;
        _gcAtStart = gc;
        return period;
    }

    public static double ThreadCpuMs(IntPtr thread) =>
        GetThreadTimes(thread, out _, out _, out var kernel, out var user) ? (kernel + user) / 10_000.0 : 0;

    private static void Reset()
    {
        Counters.Clear();
        lock (Stalls)
        {
            Stalls.Clear();
        }

        _periodStart = DateTime.UtcNow;
        _cpuAtStart = Environment.CpuUsage.TotalTime;
        _gcAtStart = GC.GetTotalPauseDuration();
    }

    private static void Add(string name, string? detail, double wallMs, double? cpuMs) =>
        Counters.GetOrAdd((name, detail), _ => new Counter()).Add(wallMs, cpuMs);

    /// <summary>The bucket of a duration: 0 up to 1 ms, 1 up to 2 ms, 2 up to 4 ms...</summary>
    public static int BucketOf(double wallMs)
    {
        var bucket = 0;
        for (var limit = 1.0; wallMs > limit && bucket < BucketCount - 1; limit *= 2)
        {
            bucket++;
        }

        return bucket;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    /// <summary>A running measurement; default when measuring is off.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly string? _name;
        private readonly string? _detail;
        private readonly long _startTicks;
        private readonly double _startCpu;
        private readonly bool _withCpu;
        private readonly string? _outerOperation;
        private readonly bool _onUiThread;

        internal Scope(string name, string? detail, bool withCpu)
        {
            _name = name;
            _detail = detail;
            _withCpu = withCpu;
            _startCpu = withCpu ? ThreadCpuMs(GetCurrentThread()) : 0;
            _onUiThread = withCpu && Environment.CurrentManagedThreadId == _uiThreadId;
            if (_onUiThread)
            {
                _outerOperation = _uiOperation;
                _uiOperation = name;
            }

            _startTicks = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_name is null)
            {
                return;
            }

            var wall = Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;
            Add(_name, _detail, wall, _withCpu ? ThreadCpuMs(GetCurrentThread()) - _startCpu : null);
            if (_onUiThread)
            {
                _uiOperation = _outerOperation;
            }
        }
    }

    private sealed class Counter
    {
        private readonly int[] _buckets = new int[BucketCount];
        private long _count;
        private double _wall;
        private double _cpu;
        private bool _hasCpu;
        private double _max;

        public void Add(double wallMs, double? cpuMs)
        {
            lock (_buckets)
            {
                _count++;
                _wall += wallMs;
                _max = Math.Max(_max, wallMs);
                _buckets[BucketOf(wallMs)]++;
                if (cpuMs is { } cpu)
                {
                    _hasCpu = true;
                    _cpu += cpu;
                }
            }
        }

        public PerfOperation ToOperation(string name, string? detail)
        {
            lock (_buckets)
            {
                return new PerfOperation(name, detail, _count, _wall, _hasCpu ? _cpu : null, _max, [.. _buckets]);
            }
        }
    }
}
