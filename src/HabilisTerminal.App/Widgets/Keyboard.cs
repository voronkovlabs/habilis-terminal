using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;

namespace HabilisTerminal.App;

/// <summary>
/// WPF's <c>Keyboard</c> on Avalonia (issue #253): the modifier keys held now (from Windows as WPF read them, #259), and the
/// focused element of the active window.
/// </summary>
internal static class Keyboard
{
    public static KeyModifiers Modifiers => Platform.Desktop.Current.HeldModifiers;

    /// <summary>The focused element of the active window of ours, or null.</summary>
    public static IInputElement? FocusedElement => ActiveWindow?.FocusManager?.GetFocusedElement();

    public static void ClearFocus() => ActiveWindow?.Focus();

    private static Window? ActiveWindow =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.FirstOrDefault(w => w.IsActive);
}
