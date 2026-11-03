using System.Windows.Input;
using HabilisTerminal.App.Input;
using HabilisTerminal.Shell.Integration;

namespace HabilisTerminal.App;

/// <summary>
/// The command line as in Far (issue #85): the focus stays on the panel and typed text goes to the command line.
/// Keys whose meaning depends on the line (Enter, Left, Right, Home, End, Del, Backspace, Esc) go to the shell, which
/// edits or runs the line when it has text and returns the key to the panel when it is empty. While a command runs,
/// the console has the keys; when the prompt is back, the panel has them again.
/// </summary>
public partial class MainWindow
{
    /// <summary>The console took the focus from a panel for the running command; the prompt gives it back.</summary>
    private bool _consoleForCommand;

    /// <summary>The prompt is giving the focus back: not a move to a panel that collapses the grown console (#82).</summary>
    private bool _returningFocus;

    private void WireCommandLine(ShellHost host)
    {
        host.PanelKeyReturned += name => Dispatcher.BeginInvoke(() => OnPanelKeyReturned(name));
        host.CommandOutputStarted += (_, _, _) => Dispatcher.BeginInvoke(() =>
        {
            if (PanelHasFocus && !IsPageOpen)
            {
                _consoleForCommand = true;
                FocusTerminal();
            }
        });
        host.StateChanged += state =>
        {
            if (state == ShellState.Idle)
            {
                Dispatcher.BeginInvoke(ReturnFocusAfterCommand);
            }
        };
    }

    private void ReturnFocusAfterCommand()
    {
        if (!_consoleForCommand)
        {
            return;
        }

        _consoleForCommand = false;
        if (!IsPageOpen && TerminalHasFocus && IsShown(_active))
        {
            _returningFocus = true;
            try
            {
                FocusActivePanel();
            }
            finally
            {
                _returningFocus = false;
            }
        }
    }

    /// <summary>A key pressed in a panel that the command line takes when it has text; false when it is the panel's.</summary>
    private bool SendToCommandLine(Key key, KeyModifiers modifiers)
    {
        if (modifiers != KeyModifiers.None || _shell?.Host.State != ShellState.Idle || PanelKeyName(key) is not { } name)
        {
            return false;
        }

        return _shell.Host.SendPanelKey(name);
    }

    /// <summary>The command line was empty: the key acts in the panel after all.</summary>
    private void OnPanelKeyReturned(string name)
    {
        if (IsPageOpen || PanelKey(name) is not { } key)
        {
            return;
        }

        // Esc on an empty line collapses the console grown over the panels first (issue #82).
        if (key == Key.Escape && OverflowExpanded)
        {
            CollapseConsole(stopGrowing: false);
            return;
        }

        if (FocusedPanel() is not { } panel)
        {
            return;
        }

        if (!HandlePluginKey(panel, key, KeyModifiers.None) && !HandleWindowKey(panel, key, KeyModifiers.None))
        {
            panel.HandleKey(key, KeyModifiers.None);
        }
    }

    private static string? PanelKeyName(Key key) => key switch
    {
        Key.Enter => "Enter",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Home => "Home",
        Key.End => "End",
        Key.Delete => "Delete",
        Key.Back => "Backspace",
        Key.Escape => "Escape",
        _ => null,
    };

    private static Key? PanelKey(string name) => name switch
    {
        "Enter" => Key.Enter,
        "Left" => Key.Left,
        "Right" => Key.Right,
        "Home" => Key.Home,
        "End" => Key.End,
        "Delete" => Key.Delete,
        "Backspace" => Key.Back,
        "Escape" => Key.Escape,
        _ => null,
    };
}
