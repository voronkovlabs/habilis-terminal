namespace HabilisTerminal.Core.Plugins;

/// <summary>
/// Hotkeys of the plugins' drive-menu items (issue #66), as in Far: F4 on an item in Alt+F1 / Alt+F2 assigns a letter
/// or digit, and that key then picks the item. Kept in the app state by plugin id and item title.
/// </summary>
public sealed class PluginHotkeys
{
    private readonly Dictionary<string, char> _keys = new(StringComparer.OrdinalIgnoreCase);

    public PluginHotkeys(IReadOnlyDictionary<string, string>? saved = null)
    {
        foreach (var (item, key) in saved ?? new Dictionary<string, string>())
        {
            if (key.Length == 1 && char.IsLetterOrDigit(key[0]))
            {
                _keys[item] = char.ToUpperInvariant(key[0]);
            }
        }
    }

    public char? Get(string pluginId, string title) => _keys.TryGetValue(Key(pluginId, title), out var key) ? key : null;

    /// <summary>
    /// Assigns <paramref name="key"/> (null removes the hotkey); another item with the same key loses it. Returns why
    /// the key cannot be used, or null when it was assigned.
    /// </summary>
    /// <param name="driveLetters">Letters of the drives in the menu: those keys already pick a drive.</param>
    public string? Assign(string pluginId, string title, char? key, IEnumerable<char> driveLetters)
    {
        var item = Key(pluginId, title);
        if (key is not { } chosen)
        {
            _keys.Remove(item);
            return null;
        }

        chosen = char.ToUpperInvariant(chosen);
        if (!char.IsAsciiLetterOrDigit(chosen))
        {
            return "A hotkey is a letter or a digit.";
        }

        if (driveLetters.Any(d => char.ToUpperInvariant(d) == chosen))
        {
            return $"{chosen} already picks drive {chosen}:.";
        }

        foreach (var other in _keys.Where(k => k.Value == chosen).Select(k => k.Key).ToList())
        {
            _keys.Remove(other);
        }

        _keys[item] = chosen;
        return null;
    }

    public IReadOnlyDictionary<string, string> Snapshot() =>
        _keys.ToDictionary(k => k.Key, k => k.Value.ToString(), StringComparer.OrdinalIgnoreCase);

    private static string Key(string pluginId, string title) => $"{pluginId}/{title}";
}
