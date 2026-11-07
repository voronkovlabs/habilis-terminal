namespace HabilisTerminal.Core.Plugins;

/// <summary>
/// Hotkeys of the drive menu's items other than drives (issues #66, #72), as in Far. Every such item gets one
/// automatically, 1, 2, 3... in menu order; F4 on an item in Alt+F1 / Alt+F2 assigns its own letter or digit instead.
/// The assigned keys are kept in the app state by an item id (a plugin id, or "places") and the item title.
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

    /// <summary>The order of automatic keys: digits, then letters (those of drives are skipped).</summary>
    private const string AutomaticKeys = "1234567890ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>The key assigned with F4, or null for an automatic one.</summary>
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

    /// <summary>
    /// The key of each item in menu order: its assigned key, else the next automatic key no drive, assigned key or
    /// earlier item uses; null when the keys run out.
    /// </summary>
    public IReadOnlyList<char?> Resolve(IReadOnlyList<(string Id, string Title, char? DefaultKey)> items, IEnumerable<char> driveLetters)
    {
        var drives = driveLetters.Select(char.ToUpperInvariant).ToHashSet();
        var assigned = items.Select(i => Get(i.Id, i.Title) is { } key && !drives.Contains(key) ? key : (char?)null).ToList();
        var used = new HashSet<char>(drives.Concat(assigned.OfType<char>()));
        var defaults = items.Select((item, index) =>
        {
            if (assigned[index] is not null || item.DefaultKey is not { } preferred)
            {
                return (char?)null;
            }

            preferred = char.ToUpperInvariant(preferred);
            return char.IsAsciiLetterOrDigit(preferred) && used.Add(preferred) ? preferred : (char?)null;
        }).ToList();
        var free = AutomaticKeys.Where(k => !used.Contains(k)).GetEnumerator();
        return [.. assigned.Select((key, index) => key ?? defaults[index] ?? (free.MoveNext() ? free.Current : (char?)null))];
    }

    public IReadOnlyDictionary<string, string> Snapshot() =>
        _keys.ToDictionary(k => k.Key, k => k.Value.ToString(), StringComparer.OrdinalIgnoreCase);

    private static string Key(string pluginId, string title) => $"{pluginId}/{title}";
}
