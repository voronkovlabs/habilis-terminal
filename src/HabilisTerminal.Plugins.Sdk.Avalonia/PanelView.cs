using Avalonia.Controls;

namespace HabilisTerminal.Plugins.Avalonia;

/// <summary>
/// A plugin panel drawn by the plugin (SDK 1.2, issue #78; on Avalonia since issue #253): an <see cref="IPanelProvider"/>
/// that also implements this shows its own control in place of the listing. The host still lists the provider for the
/// title, the info panel (Ctrl+L) and folder history, and keeps the panel keys (Tab, Ctrl+O, Ctrl+P, F10...).
/// </summary>
public interface IPanelView
{
    /// <summary>
    /// The control for the panel. Called on the UI thread once each time the provider is shown in a panel; the control
    /// is dropped when the panel leaves the provider, which is then disposed (stop timers in DisposeAsync). Read the
    /// <see cref="HostBrushes"/> keys as resources so the view follows the colour theme. Keep controls non-focusable:
    /// the keyboard belongs to the panel.
    /// </summary>
    Control CreateView(IPanelViewHost host);
}

/// <summary>What the host offers a plugin view.</summary>
public interface IPanelViewHost
{
    /// <summary>The panel's location changed or its data is new: asks the host to list the provider again.</summary>
    void Refresh();

    /// <summary>Gives the keyboard back to the panel, e.g. after a click in the view.</summary>
    void FocusPanel();
}

/// <summary>Resource keys of the host's themed brushes, in the application's resources.</summary>
public static class HostBrushes
{
    public const string Background = "Habi.Background";
    public const string Foreground = "Habi.Foreground";
    public const string Muted = "Habi.Description";
    public const string Border = "Habi.Border";
    public const string Accent = "Habi.Accent";
    public const string Selection = "Habi.Selection";
    public const string SelectionForeground = "Habi.SelectionForeground";
    public const string Progress = "Habi.Progress";
    public const string ProgressTrack = "Habi.ProgressTrack";
    public const string WidgetBackground = "Habi.WidgetBackground";
    public const string Good = "Habi.ToneGood";
    public const string Warning = "Habi.ToneWarning";
    public const string Bad = "Habi.ToneBad";
    public const string Dim = "Habi.ToneDim";
    public const string Highlight = "Habi.ToneAccent";
}
