namespace HabilisTerminal.Core.Search;

/// <summary>What a row of the results tab is.</summary>
public enum FindRowKind
{
    Folder,
    File,
    Line,
}

/// <summary>One visible row of the results tree (issue #415).</summary>
/// <param name="Key">Stable across updates: the node's path, or the file's path and the line number.</param>
/// <param name="Count">Matches below a folder or in a file: lines for a text search, files otherwise.</param>
public sealed record FindRow(FindRowKind Kind, int Depth, string Text, int Count, string Key, bool Expanded, bool CanExpand, FoundItem? Item = null, FoundLine? Line = null);

/// <summary>
/// The results of one search as Visual Studio's Find Results show them (issue #415): each root, the folders below it,
/// the files, and the matching lines of each file. Results arrive in the order the search finds them; the tree keeps
/// that order. Every node starts unfolded. Not thread-safe: the results tab adds and reads on the UI thread.
/// </summary>
public sealed class FindTree(IReadOnlyList<string> roots)
{
    private readonly List<Node> _roots = [];
    private readonly HashSet<string> _folded = new(StringComparer.OrdinalIgnoreCase);

    public int Files { get; private set; }

    public int Lines { get; private set; }

    /// <summary>Changes since the rows were last asked for.</summary>
    public bool Changed { get; private set; } = true;

    public void Add(FoundItem item)
    {
        var (root, relative) = Place(item.Path);
        var node = root;
        var parts = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            node = node.Child(parts[i]);
            if (last)
            {
                node.Item = item;
            }
        }

        if (parts.Length == 0)
        {
            root.Item = item;
        }

        var count = item.Lines is { Count: > 0 } lines ? lines.Count : 1;
        Files++;
        Lines += item.Lines?.Count ?? 0;
        for (var up = node; up is not null; up = up.Parent)
        {
            up.Count += count;
        }

        Changed = true;
    }

    /// <summary>The rows to show, folded nodes leaving out what is below them.</summary>
    public IReadOnlyList<FindRow> Rows()
    {
        Changed = false;
        var rows = new List<FindRow>();
        foreach (var root in _roots)
        {
            Flatten(root, 0, rows);
        }

        return rows;
    }

    public bool IsFolded(string key) => _folded.Contains(key);

    /// <summary>Folds or unfolds a node.</summary>
    public void SetFolded(string key, bool folded)
    {
        if (folded ? _folded.Add(key) : _folded.Remove(key))
        {
            Changed = true;
        }
    }

    /// <summary>Folds every file (its lines hide) or unfolds everything.</summary>
    public void FoldFiles(bool folded)
    {
        if (!folded)
        {
            _folded.Clear();
        }
        else
        {
            foreach (var root in _roots)
            {
                FoldFiles(root);
            }
        }

        Changed = true;
    }

    private void FoldFiles(Node node)
    {
        if (node.Item?.Lines is { Count: > 0 })
        {
            _folded.Add(node.Key);
        }

        foreach (var child in node.Children)
        {
            FoldFiles(child);
        }
    }

    private void Flatten(Node node, int depth, List<FindRow> rows)
    {
        var isFile = node.Item is { IsFolder: false } && node.Children.Count == 0;
        var lines = isFile ? node.Item!.Lines : null;
        var canExpand = node.Children.Count > 0 || lines is { Count: > 0 };
        var folded = _folded.Contains(node.Key);
        rows.Add(new FindRow(isFile ? FindRowKind.File : FindRowKind.Folder, depth, node.Name, node.Count, node.Key, canExpand && !folded, canExpand, node.Item));
        if (folded)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            Flatten(child, depth + 1, rows);
        }

        if (lines is not null)
        {
            foreach (var line in lines)
            {
                rows.Add(new FindRow(FindRowKind.Line, depth + 1, line.Text, 1, $"{node.Key}:{line.Number}", false, false, node.Item, line));
            }
        }
    }

    /// <summary>The root the path is under (the longest one), and the rest of the path.</summary>
    private (Node Root, string Relative) Place(string path)
    {
        var best = roots.Where(r => IsUnder(path, r)).OrderByDescending(r => r.Length).FirstOrDefault();
        var rootPath = best ?? (Path.GetPathRoot(path) is { Length: > 0 } drive ? drive : string.Empty);
        var root = _roots.FirstOrDefault(r => string.Equals(r.Key, rootPath, StringComparison.OrdinalIgnoreCase));
        if (root is null)
        {
            root = new Node(rootPath.TrimEnd('\\') is { Length: > 0 } name ? name : rootPath, rootPath, null);
            _roots.Add(root);
        }

        return (root, path.Length > rootPath.Length ? path[rootPath.Length..].TrimStart('\\') : string.Empty);
    }

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (path.Length == root.Length || root.EndsWith('\\') || path[root.Length] == '\\');

    private sealed class Node(string name, string key, Node? parent)
    {
        private Dictionary<string, Node>? _byName;

        public string Name { get; } = name;

        public string Key { get; } = key;

        public Node? Parent { get; } = parent;

        public List<Node> Children { get; } = [];

        public FoundItem? Item { get; set; }

        public int Count { get; set; }

        public Node Child(string childName)
        {
            _byName ??= new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
            if (!_byName.TryGetValue(childName, out var child))
            {
                child = new Node(childName, Key.TrimEnd('\\') + "\\" + childName, this);
                _byName[childName] = child;
                Children.Add(child);
            }

            return child;
        }
    }
}
