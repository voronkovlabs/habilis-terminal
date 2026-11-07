namespace HabilisTerminal.Core.FileSystem;

/// <summary>Changes collected during one batching window.</summary>
/// <param name="Names">Entry names that were created, changed, deleted or renamed (old and new names).</param>
/// <param name="RequiresFullRefresh">The watcher overflowed or failed: re-read the whole directory.</param>
public sealed record DirectoryChanges(IReadOnlyCollection<string> Names, bool RequiresFullRefresh);

/// <summary>
/// Watches one directory (not recursive) and reports changes in batches, so a burst of thousands of events
/// becomes one update (SPEC §11). Events arrive on a thread-pool thread.
/// </summary>
public sealed class DirectoryWatcher : IDisposable
{
    public static readonly TimeSpan DefaultBatchWindow = TimeSpan.FromMilliseconds(100);

    private readonly object _sync = new();
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly TimeSpan _batchWindow;
    private HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private bool _fullRefresh;
    private bool _timerArmed;
    private bool _disposed;

    public DirectoryWatcher(string path, TimeSpan? batchWindow = null)
    {
        Path = path;
        _batchWindow = batchWindow ?? DefaultBatchWindow;
        _timer = new Timer(_ => Flush());
        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = false,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes |
                           NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
        };
        _watcher.Created += (_, e) => Add(e.Name);
        _watcher.Changed += (_, e) => Add(e.Name);
        _watcher.Deleted += (_, e) => Add(e.Name);
        _watcher.Renamed += (_, e) =>
        {
            Add(e.OldName);
            Add(e.Name);
        };
        _watcher.Error += (_, _) => RequestFullRefresh();
    }

    public event Action<DirectoryChanges>? Changed;

    public string Path { get; }

    public void Start()
    {
        try
        {
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when ((ex is UnauthorizedAccessException || ex is IOException) && DirectoryReader.LinkTarget(Path) is { } target)
        {
            // A link that denies listing is read through its target (issue #176), so it is watched there too.
            _watcher.Path = target;
            _watcher.EnableRaisingEvents = true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }

        _watcher.Dispose();
        _timer.Dispose();
    }

    /// <summary>Also used when the buffer overflows (<see cref="FileSystemWatcher.Error"/>).</summary>
    internal void RequestFullRefresh()
    {
        lock (_sync)
        {
            _fullRefresh = true;
            ArmTimer();
        }
    }

    private void Add(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        lock (_sync)
        {
            _pending.Add(name);
            ArmTimer();
        }
    }

    private void ArmTimer()
    {
        if (!_timerArmed && !_disposed)
        {
            _timerArmed = true;
            _timer.Change(_batchWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        DirectoryChanges changes;
        lock (_sync)
        {
            _timerArmed = false;
            if (_disposed || (_pending.Count == 0 && !_fullRefresh))
            {
                return;
            }

            changes = new DirectoryChanges(_pending, _fullRefresh);
            _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _fullRefresh = false;
        }

        Changed?.Invoke(changes);
    }
}
