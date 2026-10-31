namespace HabilisTerminal.Core.Panels;

/// <summary>Visited directories for Alt+F12, most recent last; revisiting moves an entry to the end.</summary>
public sealed class FolderHistory(int capacity = 100)
{
    private readonly List<string> _items = [];

    /// <summary>Most recent first.</summary>
    public IReadOnlyList<string> RecentFirst => Enumerable.Reverse(_items).ToArray();

    public void Add(string path)
    {
        _items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _items.Add(path);
        if (_items.Count > capacity)
        {
            _items.RemoveAt(0);
        }
    }
}
