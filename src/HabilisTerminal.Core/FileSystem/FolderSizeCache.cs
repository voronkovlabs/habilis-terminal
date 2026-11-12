using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.Core.FileSystem;

/// <summary>A folder's totals: final, or the running total of a count in progress.</summary>
public readonly record struct FolderSize(DirectorySize.Result Totals, bool IsFinal)
{
    public long Bytes => Totals.Bytes;

    /// <summary>Some subfolders could not be read: the size is a lower bound.</summary>
    public bool IsPartial => Totals.Inaccessible > 0;
}

/// <summary>
/// Folder sizes by full path, shared by both panels, F3 and the selection totals (SPEC §6), kept for the app's
/// lifetime. A change below a folder changes the size of every folder above it, so <see cref="Invalidate"/>
/// drops the path, its ancestors and its descendants. Thread-safe: the background counter writes, the UI reads.
/// </summary>
public sealed class FolderSizeCache
{
    private const int InvalidationHistory = 256;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, FolderSize> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Path, long Stamp)> _invalidations = [];
    private long _stamp;

    /// <summary>The current invalidation stamp; a count passes it back to <see cref="Set"/> to drop stale results.</summary>
    public long Stamp
    {
        get
        {
            lock (_lock)
            {
                return _stamp;
            }
        }
    }

    /// <summary>Sizes were dropped (on the thread that invalidated): whoever shows folders asks for them again.</summary>
    public event Action<string>? Invalidated;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _sizes.Count;
            }
        }
    }

    public bool TryGet(string path, out FolderSize size)
    {
        lock (_lock)
        {
            return _sizes.TryGetValue(Normalize(path), out size);
        }
    }

    public bool HasFinal(string path) => TryGet(path, out var size) && size.IsFinal;

    /// <summary>
    /// Stores a result. With <paramref name="startStamp"/> (the <see cref="Stamp"/> read when the count started) the
    /// result is dropped if the folder, an ancestor or a descendant was invalidated since: it may be stale.
    /// A running total never replaces a final value.
    /// </summary>
    public bool Set(string path, DirectorySize.Result totals, bool isFinal = true, long? startStamp = null)
    {
        path = Normalize(path);
        lock (_lock)
        {
            if (startStamp is { } stamp && InvalidatedSince(path, stamp))
            {
                return false;
            }

            if (!isFinal && _sizes.TryGetValue(path, out var existing) && existing.IsFinal)
            {
                return false;
            }

            _sizes[path] = new FolderSize(totals, isFinal);
            return true;
        }
    }

    /// <summary>Drops the running total of a count that was stopped; a final value stays.</summary>
    public void RemoveRunning(string path)
    {
        path = Normalize(path);
        lock (_lock)
        {
            if (_sizes.TryGetValue(path, out var existing) && !existing.IsFinal)
            {
                _sizes.Remove(path);
            }
        }
    }

    /// <summary>Something changed at <paramref name="path"/>: drops it, every folder above it and everything below it.</summary>
    public void Invalidate(string path)
    {
        path = Normalize(path);
        lock (_lock)
        {
            _stamp++;
            _invalidations.Add((path, _stamp));
            if (_invalidations.Count > InvalidationHistory)
            {
                _invalidations.RemoveRange(0, _invalidations.Count - InvalidationHistory);
            }

            foreach (var key in _sizes.Keys.Where(k => Related(k, path)).ToArray())
            {
                _sizes.Remove(key);
            }
        }

        Invalidated?.Invoke(path);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _stamp++;
            _invalidations.Clear();
            _invalidations.Add((string.Empty, _stamp));
            _sizes.Clear();
        }
    }

    /// <summary>Equal, or one is inside the other.</summary>
    public static bool Related(string a, string b) => IsSameOrInside(a, b) || IsSameOrInside(b, a);

    private static bool IsSameOrInside(string path, string root) =>
        root.Length == 0 ||
        (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
         (path.Length == root.Length || PathText.IsSeparator(path[root.Length]) || PathText.IsSeparator(root[^1])));

    private bool InvalidatedSince(string path, long stamp)
    {
        if (_invalidations.Count == 0 || stamp >= _stamp)
        {
            return false;
        }

        // History lost: the oldest kept invalidation is newer than the count's start.
        if (_invalidations[0].Stamp > stamp + 1)
        {
            return true;
        }

        return _invalidations.Any(i => i.Stamp > stamp && Related(path, i.Path));
    }

    private static string Normalize(string path) => path.Length > 3 ? path.TrimEndSeparator() : path;
}
