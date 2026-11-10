using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Search;

namespace HabilisTerminal.App.Search;

/// <summary>
/// One Alt+F7 search, running in the background. It outlives its window: F3 and F4 on a result close the window, and
/// when the viewer or editor closes the same results come back with the search still going (issue #198).
/// </summary>
internal sealed class FindSession : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<FoundItem> _found = [];
    private readonly CancellationTokenSource _cancel = new();
    private string _folder = string.Empty;

    public FindSession(FindOptions options, IReadOnlyList<string> roots)
    {
        Options = options;
        Roots = roots;
        var request = new FileSearchRequest(FileMask.Parse(options.Masks), options.Text, options.MatchCase, options.WholeWords, options.Folders, roots,
            Recursive: options.Scope != SearchScope.CurrentFolderOnly);
        Task = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Unreadable = FileSearch.Run(request, Add, folder => Volatile.Write(ref _folder, folder), _cancel.Token);
            }
            catch (OperationCanceledException)
            {
                Stopped = true;
            }
        });
    }

    public FindOptions Options { get; }

    public IReadOnlyList<string> Roots { get; }

    public Task Task { get; }

    public bool IsDone => Task.IsCompleted;

    public bool Stopped { get; private set; }

    public int Unreadable { get; private set; }

    /// <summary>The folder being read now.</summary>
    public string Folder => Volatile.Read(ref _folder);

    /// <summary>The position in the list the user last chose, so it comes back on the same row.</summary>
    public int Selected { get; set; }

    /// <summary>Where paths are shown from: the root when there is one.</summary>
    public string? CommonRoot => Roots.Count == 1 ? Roots[0] : null;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _found.Count;
            }
        }
    }

    /// <summary>Results from <paramref name="from"/> on.</summary>
    public IReadOnlyList<FoundItem> Since(int from)
    {
        lock (_gate)
        {
            return from >= _found.Count ? [] : _found.GetRange(from, _found.Count - from);
        }
    }

    public string Display(FoundItem item) =>
        CommonRoot is { } root && item.Path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.GetRelativePath(root, item.Path)
            : item.Path;

    public void Stop() => _cancel.Cancel();

    public void Dispose()
    {
        _cancel.Cancel();
        _cancel.Dispose();
    }

    private void Add(FoundItem item)
    {
        lock (_gate)
        {
            _found.Add(item);
        }
    }
}
