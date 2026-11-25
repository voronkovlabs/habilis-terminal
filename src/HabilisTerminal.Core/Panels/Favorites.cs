namespace HabilisTerminal.Core.Panels;

/// <summary>
/// Favorite folders (issue #385): Ctrl+D in a panel adds the current folder or plugin location, and removes it again;
/// they are listed in the Change Drive menu in the order they were added. A folder and its path with or without a final
/// separator are the same favorite; letter case does not matter for local folders.
/// </summary>
public sealed class Favorites
{
    private readonly List<string> _items = [];

    public Favorites(IEnumerable<string>? items = null)
    {
        foreach (var item in items ?? [])
        {
            if (!string.IsNullOrWhiteSpace(item) && !Contains(item))
            {
                _items.Add(item);
            }
        }
    }

    public IReadOnlyList<string> Items => _items;

    public bool Contains(string location) => _items.Any(i => Same(i, location));

    /// <summary>Adds the location, or removes it when it is already there; true when it is a favorite now.</summary>
    public bool Toggle(string location)
    {
        if (Remove(location))
        {
            return false;
        }

        _items.Add(location);
        return true;
    }

    public bool Remove(string location) => _items.RemoveAll(i => Same(i, location)) > 0;

    /// <summary>What the menu shows: the last part of the path, or the whole location at a root.</summary>
    public static string Title(string location)
    {
        var trimmed = location.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return name.Length == 0 || name.EndsWith(':') ? location : name;
    }

    private static bool Same(string a, string b)
    {
        var x = a.TrimEnd('\\', '/');
        var y = b.TrimEnd('\\', '/');
        // Plugin addresses ("s3://bucket/Key/") keep their case; local folders do not care.
        var local = x.Length > 1 && x[1] == ':' || x.StartsWith(@"\\", StringComparison.Ordinal);
        return string.Equals(x, y, local ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
