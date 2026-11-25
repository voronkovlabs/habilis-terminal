namespace HabilisTerminal.Core.FileSystem;

/// <summary>
/// Selected entries of a panel (SPEC §5: Ins, Gray +, Gray −, Gray *). An entry is its name and whether it is a folder:
/// S3 may hold a file and a folder of the same name side by side, and marking one must not mark the other (issue #383).
/// A name marked without a kind (<see cref="Set(string, bool)"/>, <see cref="Toggle(string)"/>) covers both.
/// </summary>
public sealed class Selection
{
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _any = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _files.Count + _folders.Count + _any.Count;

    /// <summary>The marked names, each once.</summary>
    public IReadOnlyCollection<string> Names => [.. _files.Union(_folders, StringComparer.OrdinalIgnoreCase).Union(_any, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Whether a name is marked, as a file, a folder or either.</summary>
    public bool Contains(string name) => _files.Contains(name) || _folders.Contains(name) || _any.Contains(name);

    public bool Contains(FileEntry entry) => _any.Contains(entry.Name) || KindSet(entry).Contains(entry.Name);

    /// <summary>Ins on a name of either kind: toggles it; returns the new state.</summary>
    public bool Toggle(string name)
    {
        if (Contains(name))
        {
            _files.Remove(name);
            _folders.Remove(name);
            _any.Remove(name);
            return false;
        }

        _any.Add(name);
        return true;
    }

    /// <summary>Ins: toggles one entry; returns the new state.</summary>
    public bool Toggle(FileEntry entry)
    {
        var selected = !Contains(entry);
        Set(entry, selected);
        return selected;
    }

    public void Set(string name, bool selected)
    {
        if (selected)
        {
            _any.Add(name);
        }
        else
        {
            _files.Remove(name);
            _folders.Remove(name);
            _any.Remove(name);
        }
    }

    public void Set(FileEntry entry, bool selected)
    {
        // A name marked without a kind becomes this entry's mark (names marked that way come from local folders, where a
        // name is one entry only).
        _any.Remove(entry.Name);
        if (selected)
        {
            KindSet(entry).Add(entry.Name);
        }
        else
        {
            KindSet(entry).Remove(entry.Name);
        }
    }

    /// <summary>Gray +: selects matching entries. Directories are included only when asked (Far's default selects files).</summary>
    public int SelectByMask(IEnumerable<FileEntry> entries, FileMask mask, bool includeDirectories = false) =>
        entries.Count(e => (includeDirectories || !e.IsDirectory) && mask.IsMatch(e.Name) && Mark(e, true));

    /// <summary>Gray −: deselects matching entries.</summary>
    public int DeselectByMask(IEnumerable<FileEntry> entries, FileMask mask, bool includeDirectories = false) =>
        entries.Count(e => (includeDirectories || !e.IsDirectory) && mask.IsMatch(e.Name) && Mark(e, false));

    /// <summary>Gray *: inverts the selection of files (and directories when asked).</summary>
    public void Invert(IEnumerable<FileEntry> entries, bool includeDirectories = false)
    {
        foreach (var entry in entries)
        {
            if (includeDirectories || !entry.IsDirectory)
            {
                Toggle(entry);
            }
        }
    }

    public void Clear()
    {
        _files.Clear();
        _folders.Clear();
        _any.Clear();
    }

    /// <summary>Drops entries that are no longer in the listing (after a refresh).</summary>
    public void RetainExisting(IEnumerable<FileEntry> entries)
    {
        var list = entries as IReadOnlyCollection<FileEntry> ?? [.. entries];
        var files = new HashSet<string>(list.Where(e => !e.IsDirectory).Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(list.Where(e => e.IsDirectory).Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        _files.RemoveWhere(name => !files.Contains(name));
        _folders.RemoveWhere(name => !folders.Contains(name));
        _any.RemoveWhere(name => !files.Contains(name) && !folders.Contains(name));
    }

    /// <summary>Count and total size of selected files (directories count, size 0).</summary>
    public (int Files, int Directories, long Bytes) Summarize(IEnumerable<FileEntry> entries)
    {
        int files = 0, directories = 0;
        long bytes = 0;
        foreach (var entry in entries)
        {
            if (!Contains(entry))
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                directories++;
            }
            else
            {
                files++;
                bytes += entry.Length;
            }
        }

        return (files, directories, bytes);
    }

    private HashSet<string> KindSet(FileEntry entry) => entry.IsDirectory ? _folders : _files;

    /// <summary>Marks or unmarks; true when that changed something.</summary>
    private bool Mark(FileEntry entry, bool selected)
    {
        if (Contains(entry) == selected)
        {
            return false;
        }

        Set(entry, selected);
        return true;
    }
}
