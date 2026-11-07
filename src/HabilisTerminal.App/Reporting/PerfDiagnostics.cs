using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using HabilisTerminal.Core.Diagnostics;
using HabilisTerminal.Core.Reporting;

namespace HabilisTerminal.App.Reporting;

/// <summary>
/// The app's side of optional performance measuring (issue #130, setting <c>diagnostics.performance</c>, off by default):
/// a watchdog on the UI thread, periods written to local files, and a report sent to a new issue when the files reach
/// a size or from the menu. Sent files are deleted; a failed send keeps them for the next attempt.
/// </summary>
internal static class PerfDiagnostics
{
    private const long SendAtBytes = 1024 * 1024;
    private const long MaxKeptBytes = 20 * 1024 * 1024;
    private const double StallMs = 100;
    private const long ThreadStateEveryMs = 250;

    /// <summary>A stall this long gets the costly looks: its wait chain, its stack and the file probe.</summary>
    private const double LookCloserAfterMs = 300;
    private const int LookWaitMs = 1000;
    private const int MaxBusyStacks = 200;
    private const double BusyThreadMs = 250;
    private const string WatchdogName = "Performance watchdog";
    private const uint ThreadQueryLimitedInformation = 0x0800;
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SampleThreadsEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);
    private static readonly PropertyInfo? OperationName = typeof(DispatcherOperation).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static Dispatcher? _dispatcher;
    private static PerfStore? _store;
    private static Timer? _flush;
    private static Timer? _threadSampling;
    private static volatile int _lastMessage;
    private static Thread? _watchdog;
    private static volatile bool _running;
    private static volatile string? _dispatched;
    private static DateTime _nextAutomaticSend;
    private static int _sending;
    private static int _uiThreadId;
    private static StackSamplerClient? _stacks;
    private static (long Total, long Idle)? _processorsAtPeriodStart;
    private static readonly List<PerfBusyThread> BusyStacksPending = [];

    /// <summary>For tests, as no token may be used there: "sent" acts as if an issue was created, anything else as a failed send.</summary>
    private static string? DryRun => Environment.GetEnvironmentVariable("HABI_PERF_DRY_RUN") is { Length: > 0 } value ? value : null;

    public static bool Running => _running;

    /// <summary>Follows the setting; called whenever settings are applied.</summary>
    public static void Apply(bool enabled, Dispatcher dispatcher, string directory)
    {
        if (enabled == _running)
        {
            return;
        }

        if (enabled)
        {
            Start(dispatcher, directory);
        }
        else
        {
            Stop();
        }
    }

    public static void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        Perf.Stop();
        if (_dispatcher is { } dispatcher)
        {
            dispatcher.Hooks.OperationStarted -= OnOperationStarted;
            dispatcher.Hooks.OperationCompleted -= OnOperationEnded;
            dispatcher.Hooks.OperationAborted -= OnOperationEnded;
            ComponentDispatcher.ThreadFilterMessage -= OnThreadMessage;
        }

        _flush?.Dispose();
        _flush = null;
        _threadSampling?.Dispose();
        _threadSampling = null;
        Flush();
        _stacks?.Dispose();
        _stacks = null;
    }

    /// <summary>The menu's button. Null: nothing was measured yet.</summary>
    public static async Task<SendResult?> SendNowAsync()
    {
        Flush();
        return await SendPendingAsync().ConfigureAwait(true);
    }

    /// <summary>Time since the process started, the first time the user could work.</summary>
    public static void RecordStartupMilestone(string name)
    {
        if (_running)
        {
            using var process = Process.GetCurrentProcess();
            Perf.RecordMilestone(name, (DateTime.Now - process.StartTime).TotalMilliseconds);
        }
    }

    private static void Start(Dispatcher dispatcher, string directory)
    {
        _dispatcher = dispatcher;
        _store = new PerfStore(directory);
        _running = true;

        // Measuring starts while the window is being built, inside an operation the hooks did not see begin.
        _dispatched = "startup";
        Perf.Start(Environment.CurrentManagedThreadId);
        _uiThreadId = (int)GetCurrentThreadId();
        dispatcher.Hooks.OperationStarted += OnOperationStarted;
        dispatcher.Hooks.OperationCompleted += OnOperationEnded;
        dispatcher.Hooks.OperationAborted += OnOperationEnded;
        ComponentDispatcher.ThreadFilterMessage += OnThreadMessage;
        _processorsAtPeriodStart = MachineLoad.ProcessorTimes();
        _stacks = new StackSamplerClient();
        var uiThread = OpenThread(ThreadQueryLimitedInformation, false, GetCurrentThreadId());
        _watchdog = new Thread(() => Watch(dispatcher, uiThread)) { IsBackground = true, Name = WatchdogName };
        _watchdog.Start();
        _flush = new Timer(_ => FlushAndMaybeSend(), null, FlushEvery, FlushEvery);
        _threadSampling = new Timer(_ => SampleThreads(), null, SampleThreadsEvery, SampleThreadsEvery);
        StartTestStall(dispatcher);
    }

    /// <summary>Development trigger: <c>HABI_TEST_STALL=ms</c> blocks the UI thread that long, twenty seconds after the start.</summary>
    private static void StartTestStall(Dispatcher dispatcher)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("HABI_TEST_STALL"), out var ms))
        {
            var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromSeconds(20) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Thread.Sleep(ms);
            };
            timer.Start();
        }
    }

    /// <summary>The window message under way names a stall that no dispatcher operation explains. Only its number is kept.</summary>
    private static void OnThreadMessage(ref MSG msg, ref bool handled) => _lastMessage = msg.message;

    private static string? MessageName(int message) => message switch
    {
        0 => null,
        0x000F => "WM_PAINT",
        0x0100 => "WM_KEYDOWN",
        0x0101 => "WM_KEYUP",
        0x0102 => "WM_CHAR",
        0x0104 => "WM_SYSKEYDOWN",
        0x0105 => "WM_SYSKEYUP",
        0x0113 => "WM_TIMER",
        0x0200 => "WM_MOUSEMOVE",
        0x0201 => "WM_LBUTTONDOWN",
        0x0202 => "WM_LBUTTONUP",
        0x020A => "WM_MOUSEWHEEL",
        >= 0xC000 => "registered",
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"0x{message:X4}"),
    };

    private static void OnOperationStarted(object? sender, DispatcherHookEventArgs e)
    {
        try
        {
            _dispatched = OperationName?.GetValue(e.Operation) as string ?? e.Operation.GetType().Name;
        }
        catch (TargetInvocationException)
        {
            _dispatched = null;
        }
    }

    private static void OnOperationEnded(object? sender, DispatcherHookEventArgs e) => _dispatched = null;

    /// <summary>A ping on the UI thread's queue that takes long to be answered is a stall. CPU tells working from blocking.</summary>
    private static void Watch(Dispatcher dispatcher, IntPtr uiThread)
    {
        using var load = new MachineLoad();
        while (_running)
        {
            var answered = 0;
            var start = Stopwatch.GetTimestamp();
            var cpuBefore = Perf.ThreadCpuMs(uiThread);
            var collectionsBefore = GC.CollectionCount(0);
            try
            {
                dispatcher.BeginInvoke(() => Volatile.Write(ref answered, 1), DispatcherPriority.Normal);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            string? operation = null;
            string? dispatched = null;
            var sampled = false;
            var late = 0d;
            var nextState = 0L;
            Dictionary<string, int>? states = null;
            (long Total, long Idle)? processors = null;
            Task<(string? Chain, string? Stack)>? closer = null;
            while (Volatile.Read(ref answered) == 0 && _running && !dispatcher.HasShutdownStarted)
            {
                var beforeSleep = Stopwatch.GetTimestamp();
                Thread.Sleep(10);
                late = Math.Max(late, Stopwatch.GetElapsedTime(beforeSleep).TotalMilliseconds - 10);

                // What is under way when it first counts as a stall is what is most likely to blame.
                if (!sampled && Stopwatch.GetElapsedTime(start).TotalMilliseconds >= StallMs)
                {
                    sampled = true;
                    operation = Perf.UiOperation;
                    dispatched = _dispatched ?? (MessageName(_lastMessage) is { } message ? PerfReport.WindowMessagePrefix + message : null);
                    states = [];
                    processors = MachineLoad.ProcessorTimes();
                    load.Mark();
                }

                if (sampled && closer is null && Stopwatch.GetElapsedTime(start).TotalMilliseconds >= LookCloserAfterMs)
                {
                    closer = Task.Run(LookCloser);
                }

                // Listing the threads costs a few milliseconds, so only during a stall and not often.
                if (sampled && Environment.TickCount64 >= nextState)
                {
                    nextState = Environment.TickCount64 + ThreadStateEveryMs;
                    if (NativeThreads.StateOf(Environment.ProcessId, _uiThreadId) is { } state)
                    {
                        states![state] = states.GetValueOrDefault(state) + 1;
                    }
                }
            }

            var wall = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (sampled && wall >= StallMs)
            {
                var mostSeen = states is { Count: > 0 } ? states.MaxBy(pair => pair.Value).Key : null;
                var cpuUsed = Perf.ThreadCpuMs(uiThread) - cpuBefore;
                var collections = GC.CollectionCount(0) - collectionsBefore;
                var (queue, transfer) = load.ReadDisk();
                var looked = closer is not null && closer.Wait(LookWaitMs) ? closer.Result : default;
                Perf.RecordStall(new PerfStall(DateTime.UtcNow, wall, cpuUsed, collections, operation, dispatched, mostSeen, late, MemoryLoad())
                {
                    ThreadStates = states is null ? null : NativeThreads.Summarize(states),
                    WaitChain = looked.Chain,
                    Stack = looked.Stack,
                    MachineCpu = MachineLoad.BusyPercent(processors, MachineLoad.ProcessorTimes()),
                    DiskQueue = queue,
                    DiskTransferMs = transfer,
                });
            }

            Thread.Sleep(50);
        }
    }

    /// <summary>Off the watchdog, so that a slow look cannot hide the end of the stall.</summary>
    private static (string? Chain, string? Stack) LookCloser()
    {
        ProbeFile("stall");
        return (WaitChain.Describe(_uiThreadId, ThreadNameById), _stacks?.Stack(_uiThreadId));
    }

    /// <summary>
    /// How long a system font takes to open and read, in a stall and otherwise: a security product's file filter or a busy
    /// disk shows here, and the first text of a render waits in the kernel on the machines that stall.
    /// </summary>
    private static void ProbeFile(string when)
    {
        try
        {
            var font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
            using var measure = Perf.Measure("probe.font-file", when);
            using var stream = new FileStream(font, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            Span<byte> buffer = stackalloc byte[4096];
            stream.ReadExactly(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // No such font: nothing to measure.
        }
    }

    private static string? ThreadNameById(int threadId) =>
        threadId == _uiThreadId ? "UI thread" : DescriptionOf((uint)threadId);

    private static string? DescriptionOf(uint threadId)
    {
        var handle = OpenThread(ThreadQueryLimitedInformation, false, threadId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (GetThreadDescription(handle, out var text) >= 0 && text != IntPtr.Zero)
            {
                var description = Marshal.PtrToStringUni(text);
                _ = LocalFree(text);
                return string.IsNullOrWhiteSpace(description) ? null : description;
            }
        }
        finally
        {
            _ = CloseHandle(handle);
        }

        return null;
    }

    private static int? MemoryLoad()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (int)status.MemoryLoad : null;
    }

    private static void Flush()
    {
        try
        {
            SampleThreads();
            if (Perf.HasActivity)
            {
                ProbeFile("calm");
            }

            PerfPeriod? period;
            lock (ThreadGate)
            {
                // A period with no activity is not kept, so what its threads used waits for the next one.
                period = Perf.TakePeriod([.. ThreadCpuPending.Select(pair => new PerfThread(pair.Key, pair.Value))]);
                if (period is not null)
                {
                    var processors = MachineLoad.ProcessorTimes();
                    period = period with { MachineCpu = MachineLoad.BusyPercent(_processorsAtPeriodStart, processors), BusyStacks = [.. BusyStacksPending] };
                    _processorsAtPeriodStart = processors;
                    BusyStacksPending.Clear();
                    ThreadCpuPending.Clear();
                }
            }

            if (period is not null)
            {
                _store?.Append(period);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MainWindow.Trace($"performance data not written: {ex.Message}");
        }
    }

    private static readonly Lock ThreadGate = new();
    private static readonly Dictionary<string, double> ThreadCpuPending = new(StringComparer.Ordinal);
    private static Dictionary<int, double> _threadCpuSeen = [];

    /// <summary>
    /// Adds what each thread used since the last sample, by name. What a thread used after its last sample cannot be asked
    /// for once it has ended, so the report shows that as a difference from the process's total.
    /// </summary>
    private static void SampleThreads()
    {
        (int Id, string Name, double Used)? busiest = null;
        lock (ThreadGate)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                var modules = new Lazy<List<(long Start, long End, string Name)>>(() => Modules(process));
                var seenNow = new Dictionary<int, double>();
                foreach (ProcessThread thread in process.Threads)
                {
                    using (thread)
                    {
                        double cpu;
                        try
                        {
                            cpu = thread.TotalProcessorTime.TotalMilliseconds;
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                        {
                            continue; // it has just ended
                        }

                        seenNow[thread.Id] = cpu;
                        var seen = _threadCpuSeen.GetValueOrDefault(thread.Id);

                        // Less than before: Windows gave the id of an ended thread to a new one.
                        var used = cpu >= seen ? cpu - seen : cpu;
                        if (used > 0)
                        {
                            var name = ThreadName(thread, modules);
                            ThreadCpuPending[name] = ThreadCpuPending.GetValueOrDefault(name) + used;
                            if (used >= BusyThreadMs && used > (busiest?.Used ?? 0) && thread.Id != _uiThreadId && name is not (WatchdogName or Core.FileSystem.FolderSizeWorker.ThreadName))
                            {
                                busiest = (thread.Id, name, used);
                            }
                        }
                    }
                }

                _threadCpuSeen = seenNow;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                MainWindow.Trace($"threads not sampled: {ex.Message}");
            }
        }

        // Outside the lock: the helper may take a moment.
        // A thread that has gone to sleep since says nothing about where its CPU went.
        if (busiest is { } top && NativeThreads.StateOf(Environment.ProcessId, top.Id) is "running" or "ready" && _stacks?.Stack(top.Id) is { } stack)
        {
            lock (ThreadGate)
            {
                if (BusyStacksPending.Count < MaxBusyStacks)
                {
                    BusyStacksPending.Add(new PerfBusyThread(top.Name, stack, top.Used));
                }
            }
        }
    }

    private static string ThreadName(ProcessThread thread, Lazy<List<(long Start, long End, string Name)>> modules)
    {
        if (thread.Id == _uiThreadId)
        {
            return "UI thread";
        }

        if (DescriptionOf((uint)thread.Id) is { } description)
        {
            return description;
        }

        try
        {
            var start = thread.StartAddress.ToInt64();
            foreach (var (from, to, name) in modules.Value)
            {
                if (start >= from && start < to)
                {
                    return $"unnamed, {name}";
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // It has just ended.
        }

        return "unnamed";
    }

    private static List<(long Start, long End, string Name)> Modules(Process process)
    {
        var modules = new List<(long, long, string)>();
        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                using (module)
                {
                    modules.Add((module.BaseAddress.ToInt64(), module.BaseAddress.ToInt64() + module.ModuleMemorySize, module.ModuleName));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Without modules threads are only "unnamed".
        }

        return modules;
    }

    private static void FlushAndMaybeSend()
    {
        Flush();
        if (_store is not { } store)
        {
            return;
        }

        var pending = store.PendingBytes;
        if (pending > MaxKeptBytes)
        {
            // It cannot be sent, by a build with no token or for lack of a connection: measuring goes on, the disk does not fill.
            MainWindow.Trace("performance data over its limit, oldest dropped");
            DropOldest(store);
        }

        if (pending >= SendAtBytes && (ReportSender.HasToken || DryRun is not null) && DateTime.UtcNow >= _nextAutomaticSend)
        {
            _dispatcher?.BeginInvoke(async () =>
            {
                var result = await SendPendingAsync().ConfigureAwait(true);
                if (result is not { Link: not null })
                {
                    _nextAutomaticSend = DateTime.UtcNow + RetryAfter;
                }
            });
        }
    }

    private static void DropOldest(PerfStore store)
    {
        foreach (var file in new DirectoryInfo(store.Directory).EnumerateFiles("perf-*.jsonl").OrderBy(f => f.LastWriteTimeUtc).Take(1))
        {
            PerfStore.Delete([file.FullName]);
        }
    }

    private static async Task<SendResult?> SendPendingAsync()
    {
        if (_store is not { } store || Interlocked.Exchange(ref _sending, 1) == 1)
        {
            return null;
        }

        try
        {
            var (periods, files) = store.ReadPending();
            if (periods.Count == 0)
            {
                return null;
            }

            var draft = PerfReport.Build(periods, ReportSender.Version, ReportEnvironment.Windows(), Environment.ProcessorCount);
            var preview = Path.Combine(store.Directory, "last-report.md");
            File.WriteAllText(preview, draft.Body);
            var result = DryRun is { } dry
                ? new SendResult(dry == "sent" ? new IssueLink(0, "dry run", false) : null, OpenedInBrowser: false, dry == "sent" ? null : "Dry run.")
                : await ReportSender.SendFeedbackAsync(draft).ConfigureAwait(true);
            if (result.Link is not null)
            {
                // Only a created issue counts as sent: a browser page may still be closed unsent.
                PerfStore.Delete(files);
                File.Delete(preview);
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SendResult(null, OpenedInBrowser: false, ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _sending, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint threadId);

    /// <summary>A file's extension when it is short and plain; never the name.</summary>
    public static string ExtensionKind(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension.Length is > 0 and <= 8 && extension.All(char.IsAsciiLetterOrDigit) ? extension : "other";
    }
}
