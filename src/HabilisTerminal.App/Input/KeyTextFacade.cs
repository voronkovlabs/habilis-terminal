using Avalonia.Input;

namespace HabilisTerminal.App.Input;

/// <summary>The character a key produces in the current keyboard layout (for Alt+letter quick search).</summary>
public static class KeyText
{
    public static char? CharOf(Key key, bool shift) => Platform.Desktop.Current.CharOf(key, shift);
}
