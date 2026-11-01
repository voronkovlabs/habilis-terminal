using System.Runtime.InteropServices;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.Core.FileSystem;

/// <summary>
/// Sizes folders in the background for the size columns (SPEC §6): one thread at the lowest priority in Windows
/// background mode (which also lowers its disk and memory priority; thread priority alone does not), fed by a
/// <see cref="FolderSizeSchedule"/>. Results and running totals go to the shared <see cref="FolderSizeCache"/>;
/// <see cref="Updated"/> fires on the worker thread at most every <see cref="NotifyInterval"/>.
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
        var wanted = paths.Where(p => !Cache.HasFinal(p)).ToArray();
        lock (_lock)
        {
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

            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>The owner's visible folders: they go first.</summary>
    public void SetVisible(object owner, IEnumerable<string> paths)
    {
        var visible = paths.ToArray();
        lock (_lock)
        {
            _queue.SetVisible(owner, visible);
        }
    }

    /// <summary>The owner left its folder: everything it asked for is dropped, its running count stopped.</summary>
    public void Cancel(object owner)
    {
        lock (_lock)
        {
            _queue.Cancel(owner);
            DropCurrentOwner(owner);
        }
    }

    /// <summary>F3 is sizing this folder itself.</summary>
    public void Skip(string path)
    {
        lock (_lock)
        {
            _queue.Remove(path);
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
            lock (_lock)
            {
                string? next;
                IReadOnlyList<object> owners;
                while ((next = _queue.TakeNext(out owners)) is null && !_disposed)
                {
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

            Count(path, token);

            lock (_lock)
            {
                _current = null;
                _currentOwners = [];
                _currentCancel?.Dispose();
                _currentCancel = null;
            }
        }
    }

    private void Count(string path, CancellationToken token)
    {
        var stamp = Cache.Stamp;
        try
        {
            while (true)
            {
                var progress = new Progress(this, path, stamp);
                var result = DirectorySize.Compute(path, progress, token);
                if (token.IsCancellationRequested || Cache.Set(path, result, isFinal: true, startStamp: stamp))
                {
                    break;
                }

                // Something below the folder changed while it was counted (Ctrl+R, a file operation): count again.
                stamp = Cache.Stamp;
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
