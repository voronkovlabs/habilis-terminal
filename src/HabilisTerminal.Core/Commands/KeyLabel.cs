namespace HabilisTerminal.Core.Commands;

/// <summary>
/// Keys as the user reads them (issue #261): "Ctrl+Shift+P" on Windows, "⌃⇧P" on a Mac, in the order and with the
/// symbols Mac menus use (Control, Option, Shift, Command). The keys themselves stay those of Far on both.
/// </summary>
public static class KeyLabel
{
    public static string Show(string? keys) => Show(keys, OperatingSystem.IsMacOS());

    public static string Show(string? keys, bool mac)
    {
        if (string.IsNullOrEmpty(keys) || !mac || keys.StartsWith("Gray ", StringComparison.Ordinal))
        {
            return keys ?? string.Empty;
        }

        // "Ctrl+0 / Ctrl+1…5", "Ctrl+1…5, Ctrl+0" and the like: each part on its own.
        if (keys.Contains(" / ", StringComparison.Ordinal))
        {
            return string.Join(" / ", keys.Split(" / ").Select(part => Show(part, mac)));
        }

        if (keys.Contains(", ", StringComparison.Ordinal))
        {
            return string.Join(", ", keys.Split(", ").Select(part => Show(part, mac)));
        }

        var parts = keys.Split('+');
        if (parts.Length == 1 || parts[^1].Length == 0)
        {
            return Name(keys);
        }

        var modifiers = parts[..^1];
        var symbols = string.Concat(
            modifiers.Contains("Ctrl") ? "⌃" : string.Empty,
            modifiers.Contains("Alt") ? "⌥" : string.Empty,
            modifiers.Contains("Shift") ? "⇧" : string.Empty,
            modifiers.Contains("Cmd") ? "⌘" : string.Empty);
        return modifiers.All(m => m is "Ctrl" or "Alt" or "Shift" or "Cmd") ? symbols + Name(parts[^1]) : keys;
    }

    private static string Name(string key) => key switch
    {
        "Enter" => "↩",
        "Esc" => "⎋",
        "Tab" => "⇥",
        "Del" => "⌦",
        "Backspace" => "⌫",
        "Up" => "↑",
        "Down" => "↓",
        "Left" => "←",
        "Right" => "→",
        "PgUp" => "⇞",
        "PgDn" => "⇟",
        "Home" => "↖",
        "End" => "↘",
        _ => key,
    };
}
