using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;

namespace HabilisTerminal.App;

/// <summary>
/// WPF's <c>Keyboard</c> on Avalonia (issue #253): the modifier keys held now, read from Windows as WPF did, and the
/// focused element of the active window.
/// </summary>
internal static class Keyboard
{
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    public static KeyModifiers Modifiers =>
        (Down(VK_MENU) ? KeyModifiers.Alt : KeyModifiers.None) |
        (Down(VK_CONTROL) ? KeyModifiers.Control : KeyModifiers.None) |
        (Down(VK_SHIFT) ? KeyModifiers.Shift : KeyModifiers.None);

    /// <summary>The focused element of the active window of ours, or null.</summary>
    public static IInputElement? FocusedElement => ActiveWindow?.FocusManager?.GetFocusedElement();

    public static void ClearFocus() => ActiveWindow?.Focus();

    private static Window? ActiveWindow =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.FirstOrDefault(w => w.IsActive);

    private static bool Down(int key) => (GetKeyState(key) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
}
