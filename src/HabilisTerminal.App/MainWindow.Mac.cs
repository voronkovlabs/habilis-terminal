using Avalonia.Input;
using HabilisTerminal.App.Panels;

namespace HabilisTerminal.App;

/// <summary>
/// The Cmd keys of a Mac (issue #272): what Mac programs do with them, next to Far's keys, which stay. Only on macOS;
/// Windows has no Cmd key and keeps its keys as they are.
/// </summary>
public partial class MainWindow
{
    private static bool IsMac => OperatingSystem.IsMacOS();

    /// <summary>
    /// A key with Cmd held. True when the window did something with it; false leaves it to the focused control (a text
    /// box or the editor do Cmd+C / V / X / A / Z themselves). A Cmd key never reaches Far's keys as a plain letter.
    /// </summary>
    private bool HandleMacCommandKey(Key key, KeyModifiers modifiers)
    {
        var shift = modifiers == KeyModifiers.Shift;
        var plain = modifiers == KeyModifiers.None;
        var panel = FocusedPanel();
        var inConsole = panel is null && TerminalHasFocus && !IsPageOpen;
        switch (key)
        {
            case Key.Q when plain:
                Close();
                return true;
            case Key.OemComma when plain:
                OpenSettingsPage();
                return true;
            case Key.P when shift:
                Fire(ShowCommandLineAsync());
                return true;

            // As Safari and the Finder zoom: Cmd+= / Cmd+- / Cmd+0 (Ctrl+Alt+= / - / 0 stay).
            case Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract or Key.D0 or Key.NumPad0 when plain:
                return HandleZoomKey(key, KeyModifiers.Control | KeyModifiers.Alt);
        }

        if (IsPageOpen)
        {
            return false; // the viewer and the editor have their own keys
        }

        switch (key)
        {
            case Key.T when plain:
                NewTab();
                return true;
            case Key.W when plain && _tabs.Count > 1 && _tab is { } tab:
                CloseTab(tab);
                return true;
            case Key.F when plain:
                FindFiles();
                return true;
        }

        if (inConsole)
        {
            switch (key)
            {
                case Key.C when plain:
                    CopyTerminalSelection();
                    return true;
                case Key.V when plain:
                    PasteIntoTerminal();
                    return true;
                case Key.K when plain:
                    ClearConsole();
                    return true;
                default:
                    return true; // no Cmd key types into the shell
            }
        }

        if (panel is null)
        {
            return false;
        }

        // The Finder's keys in a panel.
        switch (key)
        {
            case Key.C when plain:
                CopyNames(panel, fullPaths: true);
                return true;
            case Key.V when plain:
                PasteIntoTerminal();
                return true;
            case Key.A when plain:
                PanelKey(panel, Key.Add, KeyModifiers.Shift); // Far's Shift+Gray +
                return true;
            case Key.Back when plain:
                PanelKey(panel, Key.F8, KeyModifiers.None); // to the Trash, as deleting does by default
                return true;
            case Key.Up when plain:
                PanelKey(panel, Key.PageUp, KeyModifiers.Control); // the folder above
                return true;
            case Key.Down or Key.O when plain:
                PanelKey(panel, Key.Enter, KeyModifiers.None);
                return true;
            case Key.OemPeriod when shift:
                PanelKey(panel, Key.H, KeyModifiers.Control); // hidden files, as Cmd+Shift+. in the Finder
                return true;
            case Key.N when shift:
                PanelKey(panel, Key.F7, KeyModifiers.None); // a new folder
                return true;
            case Key.I when plain:
                PanelKey(panel, Key.A, KeyModifiers.Control); // Get Info: the attributes
                return true;
            case Key.R when plain:
                PanelKey(panel, Key.R, KeyModifiers.Control); // read again
                return true;
            default:
                return true;
        }
    }

    /// <summary>Cmd+K, as in Terminal: the scrollback goes, and the shell draws its prompt at the top.</summary>
    private void ClearConsole()
    {
        if (_tab is not { } tab)
        {
            return;
        }

        tab.Connection.Emulator.Feed("\u001b[H\u001b[2J\u001b[3J");
        tab.Host.Write("\f");
    }

    /// <summary>Cmd+click marks a file, as Insert does in Far: a Mac keyboard has no Insert.</summary>
    internal static KeyModifiers MarkModifier => IsMac ? KeyModifiers.Meta : KeyModifiers.Control;
}
