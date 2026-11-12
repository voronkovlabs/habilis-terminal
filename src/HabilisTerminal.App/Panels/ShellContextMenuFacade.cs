using Avalonia;
using Avalonia.Controls;

namespace HabilisTerminal.App.Panels;

/// <summary>The system's context menu for items of one folder (SPEC §6, issue #41): Explorer's on Windows, none elsewhere yet (#259).</summary>
public static class ShellContextMenu
{
    /// <summary>Shows the menu at <paramref name="screen"/> (physical pixels) and runs the chosen command.</summary>
    /// <param name="intercept">Gets the chosen command's verb first (e.g. "rename"); returns true when it handled it.</param>
    /// <returns>True when a command was chosen.</returns>
    public static bool Show(Window owner, string folder, IReadOnlyList<string> names, PixelPoint screen, Func<string, bool>? intercept = null) =>
        Platform.Desktop.Current.ShowContextMenu(owner, folder, names, screen, intercept);
}
