namespace HabilisTerminal.Core.FileSystem;

/// <summary>Applies watcher changes to a listing without re-reading the whole directory.</summary>
public static class ListingUpdater
{
    /// <summary>
    /// Changes larger than this are cheaper to apply by re-reading the directory than by one stat per name.
    /// </summary>
    public const int MaxIncrementalChanges = 512;

    /// <summary>
    /// Returns the updated listing (enumeration order: existing entries keep their place, new ones are appended),
    /// or null when the caller should re-read the directory.
    /// </summary>
    public static FileEntry[]? Apply(string directory, IReadOnlyList<FileEntry> entries, DirectoryChanges changes)
    {
        if (changes.RequiresFullRefresh || changes.Names.Count > MaxIncrementalChanges)
        {
            return null;
        }

        var updated = new Dictionary<string, FileEntry?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in changes.Names)
        {
            updated[name] = DirectoryReader.ReadEntry(directory, name);
        }

        var result = new List<FileEntry>(entries.Count + updated.Count);
        foreach (var entry in entries)
        {
            if (!updated.Remove(entry.Name, out var replacement))
            {
                result.Add(entry);
            }
            else if (replacement is { } current)
            {
                result.Add(current);
            }
        }

        foreach (var added in updated.Values)
        {
            if (added is { } entry)
            {
                result.Add(entry);
            }
        }

        return result.ToArray();
    }
}
