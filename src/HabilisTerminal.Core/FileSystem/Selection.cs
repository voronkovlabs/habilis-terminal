namespace HabilisTerminal.Core.FileSystem;

/// <summary>Selected entries of a panel, by name (SPEC §5: Ins, Gray +, Gray −, Gray *).</summary>
public sealed class Selection
{
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _names.Count;

    public IReadOnlyCollection<string> Names => _names;

    public bool Contains(string name) => _names.Contains(name);

    /// <summary>Ins: toggles one entry; returns the new state.</summary>
    public bool Toggle(string name)
    {
        if (_names.Remove(name))
        {
            return false;
        }

        _names.Add(name);
        return true;
    }

    public void Set(string name, bool selected)
    {
        if (selected)
        {
            _names.Add(name);
        }
        else
        {
            _names.Remove(name);
        }
    }

    /// <summary>Gray +: selects matching entries. Directories are included only when asked (Far's default selects files).</summary>
    public int SelectByMask(IEnumerable<FileEntry> entries, FileMask mask, bool includeDirectories = false) =>
        entries.Count(e => (includeDirectories || !e.IsDirectory) && mask.IsMatch(e.Name) && _names.Add(e.Name));

    /// <summary>Gray −: deselects matching entries.</summary>
    public int DeselectByMask(IEnumerable<FileEntry> entries, FileMask mask, bool includeDirectories = false) =>
        entries.Count(e => (includeDirectories || !e.IsDirectory) && mask.IsMatch(e.Name) && _names.Remove(e.Name));

    /// <summary>Gray *: inverts the selection of files (and directories when asked).</summary>
    public void Invert(IEnumerable<FileEntry> entries, bool includeDirectories = false)
    {
        foreach (var entry in entries)
        {
            if (includeDirectories || !entry.IsDirectory)
            {
                Toggle(entry.Name);
            }
        }
    }

    public void Clear() => _names.Clear();

    /// <summary>Drops names that are no longer in the listing (after a refresh).</summary>
    public void RetainExisting(IEnumerable<FileEntry> entries)
    {
        var existing = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        _names.RemoveWhere(name => !existing.Contains(name));
    }

    /// <summary>Count and total size of selected files (directories count, size 0).</summary>
    public (int Files, int Directories, long Bytes) Summarize(IEnumerable<FileEntry> entries)
    {
        int files = 0, directories = 0;
        long bytes = 0;
        foreach (var entry in entries)
        {
            if (!_names.Contains(entry.Name))
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
}
