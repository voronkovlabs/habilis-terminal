using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace HabilisTerminal.App.Widgets;

/// <summary>
/// Where the main window's popups live while they exist (issue #253). They draw in the window's overlay layer, which
/// takes only popups that are in the window's tree; the window gives a canvas of no size for them.
/// </summary>
internal static class PopupParking
{
    private static Panel? s_host;

    public static void Register(Panel host) => s_host = host;

    /// <summary>Puts the popup in the window's tree; with <paramref name="whileOpen"/> it leaves again when it closes.</summary>
    public static void Park(Popup popup, bool whileOpen)
    {
        if (s_host is not { } host || popup.Parent is not null)
        {
            return;
        }

        host.Children.Add(popup);
        if (whileOpen)
        {
            popup.Closed += (_, _) => host.Children.Remove(popup);
        }
    }
}
