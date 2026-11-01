namespace HabilisTerminal.Core.FileSystem;

/// <summary>
/// Which folder the background counter sizes next (SPEC §6). Each owner (a panel) asks for the folders of its
/// listing in display order and tells which of them are visible. Visible folders go first, the owner that scrolled
/// most recently before the other; then the rest in listing order. A folder two owners ask for is counted once.
/// Not thread-safe: <see cref="FolderSizeWorker"/> locks around it.
/// </summary>
public sealed class FolderSizeSchedule
{
    private readonly List<Request> _requests = [];
    private long _clock;

    public bool IsEmpty => _requests.All(r => r.Pending.Count == 0);

    /// <summary>Replaces what <paramref name="owner"/> asks for.</summary>
    public void Set(object owner, IEnumerable<string> paths)
    {
        var request = Find(owner);
        if (request is null)
        {
            request = new Request(owner);
            _requests.Add(request);
        }

        request.Order.Clear();
        request.Pending.Clear();
        request.Next = 0;
        foreach (var path in paths)
        {
            if (request.Pending.Add(path))
            {
                request.Order.Add(path);
            }
        }

        request.Touched = ++_clock;
    }

    /// <summary>The visible part of the owner's listing (scroll re-orders, it does not cancel).</summary>
    public void SetVisible(object owner, IEnumerable<string> paths)
    {
        if (Find(owner) is { } request)
        {
            request.Visible.Clear();
            request.Visible.AddRange(paths);
            request.Touched = ++_clock;
        }
    }

    /// <summary>The owner left its folder: forget what it asked for.</summary>
    public void Cancel(object owner) => _requests.RemoveAll(r => r.Owner == owner);

    public bool IsWanted(string path) => _requests.Any(r => r.Pending.Contains(path));

    /// <summary>The folder was sized elsewhere (F3): nobody waits for it any more.</summary>
    public void Remove(string path)
    {
        foreach (var request in _requests)
        {
            request.Pending.Remove(path);
        }
    }

    /// <summary>The next folder to count and the owners waiting for it; null when nothing is pending.</summary>
    public string? TakeNext(out IReadOnlyList<object> owners)
    {
        var ordered = _requests.OrderByDescending(r => r.Touched).ToArray();
        var next = ordered.SelectMany(r => r.Visible.Where(r.Pending.Contains)).FirstOrDefault()
            ?? ordered.Select(r => r.FirstPending()).FirstOrDefault(p => p is not null);
        if (next is null)
        {
            owners = [];
            return null;
        }

        owners = _requests.Where(r => r.Pending.Contains(next)).Select(r => r.Owner).ToArray();
        Remove(next);
        return next;
    }

    /// <summary>
    /// True when a visible folder outranks <paramref name="current"/>, the folder being counted for
    /// <paramref name="owners"/>: the count should pause (#37). Same order as <see cref="TakeNext"/>, with the
    /// current folder counted as still pending for its owners; folders out of view never interrupt a count.
    /// </summary>
    public bool ShouldYield(string current, IReadOnlyCollection<object> owners)
    {
        foreach (var request in _requests.OrderByDescending(r => r.Touched))
        {
            foreach (var path in request.Visible)
            {
                if (owners.Contains(request.Owner) && string.Equals(path, current, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (request.Pending.Contains(path))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A paused count: the folder is pending again for the owners that still ask for it.</summary>
    public void Restore(string path, IEnumerable<object> owners)
    {
        foreach (var request in _requests.Where(r => owners.Contains(r.Owner) && r.Order.Contains(path, StringComparer.OrdinalIgnoreCase)))
        {
            request.Pending.Add(path);
            request.Next = 0;
        }
    }

    private Request? Find(object owner) => _requests.FirstOrDefault(r => r.Owner == owner);

    private sealed class Request(object owner)
    {
        public object Owner { get; } = owner;

        public List<string> Order { get; } = [];

        public HashSet<string> Pending { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Visible { get; } = [];

        public long Touched { get; set; }

        /// <summary>Index into <see cref="Order"/> before which nothing is pending (amortised linear scan).</summary>
        public int Next { get; set; }

        public string? FirstPending()
        {
            while (Next < Order.Count && !Pending.Contains(Order[Next]))
            {
                Next++;
            }

            return Next < Order.Count ? Order[Next] : null;
        }
    }
}
