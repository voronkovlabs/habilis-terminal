using System.Windows.Input;

namespace HabilisTerminal.App.Input;

/// <summary>Keys written as text, e.g. "Shift+F3", "Ctrl+F", "Del", "Alt+1" (plugin command keys, issue #49).</summary>
public static class KeyNames
{
    public static bool TryParse(string text, out Key key, out KeyModifiers modifiers)
    {
        key = Key.None;
        modifiers = KeyModifiers.None;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        foreach (var part in parts[..^1])
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= KeyModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= KeyModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= KeyModifiers.Shift;
                    break;
                default:
                    return false;
            }
        }

        var name = parts[^1];
        key = name.ToUpperInvariant() switch
        {
            "DEL" => Key.Delete,
            "INS" => Key.Insert,
            "ESC" => Key.Escape,
            "PGUP" => Key.PageUp,
            "PGDN" => Key.PageDown,
            { Length: 1 } single when char.IsAsciiDigit(single[0]) => Key.D0 + (single[0] - '0'),
            _ => Enum.TryParse<Key>(name, ignoreCase: true, out var parsed) ? parsed : Key.None,
        };
        return key != Key.None;
    }
}
