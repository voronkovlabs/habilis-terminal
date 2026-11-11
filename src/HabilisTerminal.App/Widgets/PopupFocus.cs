using Avalonia.Controls.Primitives;

namespace HabilisTerminal.App.Widgets;

/// <summary>
/// Opens popups that take the keyboard. A popup gets its keys through the main window, while the Win32 focus normally
/// stays in the console (issue #90), so the window moves the focus for as long as such a popup is open (issue #92).
/// </summary>
public static class PopupFocus
{
    /// <summary>A keyboard popup is about to open.</summary>
    public static event Action? Opening;

    /// <summary>A keyboard popup has closed.</summary>
    public static event Action? Closed;

    public static void Open(Popup popup)
    {
        Opening?.Invoke();
        PopupParking.Park(popup, whileOpen: true);
        popup.Closed += (_, _) => Closed?.Invoke();
        popup.IsOpen = true;
    }
}
