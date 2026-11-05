using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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
    private const uint ThreadQueryLimitedInformation = 0x0800;
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);
    private static readonly PropertyInfo? OperationName = typeof(DispatcherOperation).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static Dispatcher? _dispatcher;
    private static PerfStore? _store;
    private static Timer? _flush;
    private static Thread? _watchdog;
    private static volatile bool _running;
    private static volatile string? _dispatched;
    private static DateTime _nextAutomaticSend;
    private static int _sending;
    private static int _uiThreadId;

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
        }

        _flush?.Dispose();
        _flush = null;
        Flush();
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
        var uiThread = OpenThread(ThreadQueryLimitedInformation, false, GetCurrentThreadId());
        _watchdog = new Thread(() => Watch(dispatcher, uiThread)) { IsBackground = true, Name = "Performance watchdog" };
        _watchdog.Start();
        _flush = new Timer(_ => FlushAndMaybeSend(), null, FlushEvery, FlushEvery);
    }

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
            while (Volatile.Read(ref answered) == 0 && _running && !dispatcher.HasShutdownStarted)
            {
                Thread.Sleep(10);

                // What is under way when it first counts as a stall is what is most likely to blame.
                if (!sampled && Stopwatch.GetElapsedTime(start).TotalMilliseconds >= StallMs)
                {
                    sampled = true;
                    operation = Perf.UiOperation;
                    dispatched = _dispatched;
                }
            }

            var wall = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (sampled && wall >= StallMs)
            {
                Perf.RecordStall(wall, Perf.ThreadCpuMs(uiThread) - cpuBefore, GC.CollectionCount(0) - collectionsBefore, operation, dispatched);
            }

            Thread.Sleep(50);
        }
    }

    private static void Flush()
    {
        try
        {
            if (Perf.TakePeriod(ThreadCpu()) is { } period)
            {
                _store?.Append(period);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MainWindow.Trace($"performance data not written: {ex.Message}");
        }
    }

    private static readonly Dictionary<int, double> ThreadCpuSeen = [];

    /// <summary>CPU per thread since the last call, by name. What ended meanwhile cannot be asked, so it shows as a difference.</summary>
    private static List<PerfThread> ThreadCpu()
    {
        var byName = new Dictionary<string, double>(StringComparer.Ordinal);
        using var process = Process.GetCurrentProcess();
        var modules = Modules(process);
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

                _ = ThreadCpuSeen.TryGetValue(thread.Id, out var seen);
                ThreadCpuSeen[thread.Id] = cpu;
                var name = ThreadName(thread, modules);
                byName[name] = byName.GetValueOrDefault(name) + (cpu - seen);
            }
        }

        return [.. byName.Select(pair => new PerfThread(pair.Key, pair.Value))];
    }

    private static string ThreadName(ProcessThread thread, List<(long Start, long End, string Name)> modules)
    {
        if (thread.Id == _uiThreadId)
        {
            return "UI thread";
        }

        var handle = OpenThread(ThreadQueryLimitedInformation, false, (uint)thread.Id);
        if (handle != IntPtr.Zero)
        {
            try
            {
                if (GetThreadDescription(handle, out var text) >= 0 && text != IntPtr.Zero)
                {
                    var description = Marshal.PtrToStringUni(text);
                    _ = LocalFree(text);
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        return description;
                    }
                }
            }
            finally
            {
                _ = CloseHandle(handle);
            }
        }

        try
        {
            var start = thread.StartAddress.ToInt64();
            foreach (var (from, to, name) in modules)
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
