using System.Collections.Immutable;
using System.Runtime.InteropServices;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.Core.FileSystem;

/// <summary>
/// Sizes folders in the background for the size columns (SPEC §6): one thread at the lowest priority in Windows
/// background mode (which also lowers its disk and memory priority; thread priority alone does not), fed by a
/// <see cref="FolderSizeSchedule"/>. Results and running totals go to the shared <see cref="FolderSizeCache"/>;
/// <see cref="Updated"/> fires on the worker thread at most every <see cref="NotifyInterval"/>.
/// A count pauses between folders when a visible folder that ranks higher is waiting (the other panel, a newly
/// opened folder); it goes back to the schedule, keeps showing its running total and later continues where it
/// stopped (#37).
/// </summary>
public sealed class FolderSizeWorker : IDisposable
{
    public static readonly TimeSpan NotifyInterval = TimeSpan.FromMilliseconds(150);

    private const int ThreadModeBackgroundBegin = 0x00010000;

    private readonly object _lock = new(); // Monitor.Wait needs an object, not a Lock
    private readonly FolderSizeSchedule _queue = new();
    private readonly Thread _thread;
    private readonly bool _background;
    private string? _current;
    private HashSet<object> _currentOwners = [];
    private CancellationTokenSource? _currentCancel;
    private readonly Dictionary<string, Paused> _paused = new(StringComparer.OrdinalIgnoreCase);
    private ImmutableDictionary<object, VisibleList> _visible = ImmutableDictionary<object, VisibleList>.Empty;
    private long _visibleOrder;
    private volatile bool _scheduleChanged;
    private bool _disposed;
    private long _nextNotify;
    private bool _dirty;

    public FolderSizeWorker(FolderSizeCache cache, bool backgroundMode = true)
    {
        Cache = cache;
        _background = backgroundMode;
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "Folder sizes" };
        _thread.Start();
    }

    /// <summary>Sizes changed (a folder finished, or running totals grew). Raised on the worker thread.</summary>
    public event Action? Updated;

    public FolderSizeCache Cache { get; }

    /// <summary>The folder being counted now (for tests and the trace).</summary>
    public string? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Folders whose count is paused, to continue later (for tests).</summary>
    public int PausedCount
    {
        get
        {
            lock (_lock)
            {
                return _paused.Count;
            }
        }
    }

    /// <summary>Called on the worker thread after each folder read (tests hold a count here).</summary>
    internal Action<string>? FolderRead { get; set; }

    /// <summary>True while folders are pending or being counted.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_lock)
            {
                return _current is not null || !_queue.IsEmpty;
            }
        }
    }

    /// <summary>
    /// Automatic counting applies to local fixed drives only; network, removable and optical drives are sized by F3
    /// alone (they can be slow or go away).
    /// </summary>
    public static bool IsAutoCountable(string path)
    {
        if (string.IsNullOrEmpty(path) || LongPath.IsUnc(path) || Path.GetPathRoot(path) is not { Length: > 0 } root)
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// <paramref name="owner"/> shows these folders (full paths, display order); replaces its earlier request.
    /// Folders already sized are skipped. A folder being counted that the owner no longer wants is stopped unless
    /// another owner still waits for it.
    /// </summary>
    public void Request(object owner, IEnumerable<string> paths)
    {
        using var measure = Diagnostics.Perf.Measure("folder.size.request");
        var wanted = paths.Where(p => !Cache.HasFinal(p)).ToArray();
        lock (_lock)
        {
            ApplyVisible();
            _queue.Set(owner, wanted);
            if (_current is { } current)
            {
                if (wanted.Contains(current, StringComparer.OrdinalIgnoreCase))
                {
                    _queue.Remove(current);
                    _currentOwners.Add(owner);
                }
                else
                {
                    DropCurrentOwner(owner);
                }
            }

            DropUnwantedPaused();
            _scheduleChanged = true;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// The owner's visible folders: they go first. Called while a panel draws, so it must not wait for the lock: this
    /// thread holds it at the lowest priority and may be held up itself, which would freeze the window (issue #136).
    /// The worker takes the newest list of each owner when it next looks at the schedule.
    /// </summary>
    public void SetVisible(object owner, IEnumerable<string> paths)
    {
        var visible = new VisibleList(Interlocked.Increment(ref _visibleOrder), [.. paths]);
        ImmutableInterlocked.AddOrUpdate(ref _visible, owner, visible, (_, _) => visible);
        _scheduleChanged = true;
    }

    /// <summary>The owner left its folder: everything it asked for is dropped, its running count stopped.</summary>
    public void Cancel(object owner)
    {
        using var measure = Diagnostics.Perf.Measure("folder.size.cancel");
        ImmutableInterlocked.TryRemove(ref _visible, owner, out _);
        lock (_lock)
        {
            ApplyVisible();
            _queue.Cancel(owner);
            DropCurrentOwner(owner);
            DropUnwantedPaused();
        }
    }

    /// <summary>F3 is sizing this folder itself.</summary>
    public void Skip(string path)
    {
        lock (_lock)
        {
            _queue.Remove(path);
            DropUnwantedPaused();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _currentCancel?.Cancel();
            Monitor.PulseAll(_lock);
        }
    }

    private void DropCurrentOwner(object owner)
    {
        if (_current is not null && _currentOwners.Remove(owner) && _currentOwners.Count == 0)
        {
            _currentCancel?.Cancel();
        }
    }

    /// <summary>A paused count nobody asks for any more is dropped with its running total.</summary>
    private void DropUnwantedPaused()
    {
        foreach (var path in _paused.Keys.Where(p => !_queue.IsWanted(p)).ToArray())
        {
            _paused.Remove(path);
            Cache.RemoveRunning(path);
        }
    }

    private void Run()
    {
        if (_background)
        {
            SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
        }

        while (true)
        {
            string path;
            CancellationToken token;
            Paused? resumed;
            lock (_lock)
            {
                string? next;
                IReadOnlyList<object> owners;
                while (true)
                {
                    ApplyVisible();
                    if ((next = _queue.TakeNext(out owners)) is not null || _disposed)
                    {
                        break;
                    }

                    if (_dirty)
                    {
                        break;
                    }

                    Monitor.Wait(_lock);
                }

                if (_disposed)
                {
                    return;
                }

                if (next is null)
                {
                    // Idle with unreported changes: report them, then wait again.
                    _dirty = false;
                    _nextNotify = Environment.TickCount64 + (long)NotifyInterval.TotalMilliseconds;
                    Monitor.Exit(_lock);
                    try
                    {
                        Updated?.Invoke();
                    }
                    finally
                    {
                        Monitor.Enter(_lock);
                    }

                    continue;
                }

                _paused.Remove(next, out resumed);
                if (Cache.HasFinal(next))
                {
                    continue; // F3 sized it meanwhile
                }

                path = next;
                _current = path;
                _currentOwners = [.. owners];
                _currentCancel = new CancellationTokenSource();
                token = _currentCancel.Token;
            }

            Count(path, resumed, token);

            lock (_lock)
            {
                _current = null;
                _currentOwners = [];
                _currentCancel?.Dispose();
                _currentCancel = null;
            }
        }
    }

    private sealed record VisibleList(long Order, string[] Paths);

    private void Count(string path, Paused? resumed, CancellationToken token)
    {
        using var measure = Diagnostics.Perf.Enabled ? Diagnostics.Perf.Measure("folder.size", Diagnostics.Perf.DriveKind(path)) : default;
        var stamp = resumed?.Stamp ?? Cache.Stamp;
        var walk = resumed?.Walk ?? new DirectorySize.Walk(path);
        try
        {
            while (true)
            {
                var progress = new Progress(this, path, stamp);
                if (!walk.Run(progress, ShouldPause, token))
                {
                    Pause(path, walk, stamp);
                    break;
                }

                if (token.IsCancellationRequested || Cache.Set(path, walk.Totals, isFinal: true, startStamp: stamp))
                {
                    break;
                }

                // Something below the folder changed while it was counted (Ctrl+R, a file operation): count again.
                stamp = Cache.Stamp;
                walk = new DirectorySize.Walk(path);
            }

            if (token.IsCancellationRequested)
            {
                Cache.RemoveRunning(path);
            }
        }
        catch (OperationCanceledException)
        {
            Cache.RemoveRunning(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Cache.Set(path, new DirectorySize.Result(0, 0, 0, 1), isFinal: true, startStamp: stamp);
        }

        Changed();
    }

    /// <summary>Under the lock: brings the schedule up to date with <see cref="SetVisible"/>, in the order of the calls.</summary>
    private void ApplyVisible()
    {
        var pending = Interlocked.Exchange(ref _visible, ImmutableDictionary<object, VisibleList>.Empty);
        foreach (var (owner, list) in pending.OrderBy(entry => entry.Value.Order))
        {
            _queue.SetVisible(owner, list.Paths);
        }
    }

    /// <summary>After each folder: pause when the schedule changed and a visible folder now ranks higher.</summary>
    private bool ShouldPause()
    {
        if (FolderRead is { } folderRead)
        {
            folderRead(Current ?? string.Empty);
        }

        if (!_scheduleChanged)
        {
            return false;
        }

        lock (_lock)
        {
            _scheduleChanged = false;
            ApplyVisible();
            return _current is { } current && _queue.ShouldYield(current, _currentOwners);
        }
    }

    /// <summary>Keeps the walk for later and puts the folder back in the schedule; its running total stays shown.</summary>
    private void Pause(string path, DirectorySize.Walk walk, long stamp)
    {
        Cache.Set(path, walk.Totals, isFinal: false, startStamp: stamp);
        lock (_lock)
        {
            if (_currentCancel?.IsCancellationRequested == true || _disposed)
            {
                return; // stopped meanwhile: the caller drops the running total
            }

            _paused[path] = new Paused(walk, stamp);
            _queue.Restore(path, _currentOwners);
            DropUnwantedPaused();
        }
    }

    /// <summary>Notes a change and reports it when the interval has passed; the rest is reported when idle.</summary>
    private void Changed()
    {
        bool notify;
        lock (_lock)
        {
            var now = Environment.TickCount64;
            notify = now >= _nextNotify;
            if (notify)
            {
                _nextNotify = now + (long)NotifyInterval.TotalMilliseconds;
                _dirty = false;
            }
            else
            {
                _dirty = true;
            }
        }

        if (notify)
        {
            Updated?.Invoke();
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    private sealed record Paused(DirectorySize.Walk Walk, long Stamp);

    /// <summary>Running totals into the cache, at most every notify interval.</summary>
    private sealed class Progress(FolderSizeWorker worker, string path, long stamp) : IProgress<DirectorySize.Result>
    {
        private long _next = Environment.TickCount64 + (long)NotifyInterval.TotalMilliseconds;

        public void Report(DirectorySize.Result value)
        {
            var now = Environment.TickCount64;
            if (now < _next)
            {
                return;
            }

            _next = now + (long)NotifyInterval.TotalMilliseconds;
            if (worker.Cache.Set(path, value, isFinal: false, startStamp: stamp))
            {
                worker.Changed();
            }
        }
    }
}
