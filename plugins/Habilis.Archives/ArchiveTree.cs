namespace Habilis.Archives;

/// <summary>A file or folder of an archive folder; <see cref="Item"/> is null for a folder only implied by paths.</summary>
public sealed record ArchiveNode(string Name, string Path, bool IsFolder, long Size, DateTime? ModifiedUtc, ArchiveItem? Item);

/// <summary>
/// The folders of an archive from its flat item list. Folders that have no item of their own (zip files often store
/// only file paths) are implied by the paths under them. Names compare case-insensitively, as in Windows.
/// </summary>
public sealed class ArchiveTree
{
    private readonly Dictionary<string, Dictionary<string, ArchiveNode>> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<ArchiveItem> _items;

    public ArchiveTree(IReadOnlyList<ArchiveItem> items)
    {
        _items = items;
        _folders[string.Empty] = new(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var parent = AddFolders(item.Path);
            var name = Name(item.Path);
            var folder = _folders[parent];
            folder[name] = new ArchiveNode(name, item.Path, item.IsFolder, item.IsFolder ? 0 : item.Size, item.ModifiedUtc, item);
            if (item.IsFolder)
            {
                _folders.TryAdd(item.Path, new(StringComparer.OrdinalIgnoreCase));
            }
        }
    }

    public int FileCount => _items.Count(i => !i.IsFolder);

    public int FolderCount => _folders.Count - 1;

    public long TotalSize => _items.Where(i => !i.IsFolder).Sum(i => i.Size);

    public bool IsFolder(string path) => _folders.ContainsKey(path);

    /// <summary>The entries of a folder ("" is the root); empty for a path that is not a folder.</summary>
    public IReadOnlyCollection<ArchiveNode> List(string folder) =>
        _folders.TryGetValue(folder, out var entries) ? entries.Values : [];

    public ArchiveNode? Find(string path) =>
        _folders.TryGetValue(Parent(path), out var entries) && entries.TryGetValue(Name(path), out var node) ? node : null;

    /// <summary>The items inside a folder, at any depth.</summary>
    public IEnumerable<ArchiveItem> ItemsUnder(string folder)
    {
        var prefix = folder + "\\";
        return _items.Where(i => i.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The folders inside a folder, at any depth, including those only implied by paths.</summary>
    public IEnumerable<string> FoldersUnder(string folder)
    {
        var prefix = folder + "\\";
        return _folders.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public static string Parent(string path)
    {
        var cut = path.LastIndexOf('\\');
        return cut < 0 ? string.Empty : path[..cut];
    }

    public static string Name(string path) => path[(path.LastIndexOf('\\') + 1)..];

    public static string Combine(string folder, string name) => folder.Length == 0 ? name : folder + "\\" + name;

    /// <summary>Makes sure every folder above <paramref name="path"/> exists; returns the parent folder.</summary>
    private string AddFolders(string path)
    {
        var parent = Parent(path);
        if (parent.Length == 0 || _folders.ContainsKey(parent))
        {
            return parent;
        }

        var grandParent = AddFolders(parent);
        _folders[parent] = new(StringComparer.OrdinalIgnoreCase);
        var name = Name(parent);
        _folders[grandParent].TryAdd(name, new ArchiveNode(name, parent, IsFolder: true, 0, null, null));
        return parent;
    }
}
