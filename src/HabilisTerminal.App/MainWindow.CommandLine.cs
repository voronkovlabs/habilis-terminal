using System.Windows.Input;
using HabilisTerminal.App.Input;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Shell.Integration;

namespace HabilisTerminal.App;

/// <summary>
/// The command line as in Far (issues #85, #90). The Win32 focus always stays in the console, so everything about the
/// command line works natively (caret, selection, completion, paste, the profile's key bindings). The active panel
/// "has the keys" as a state of its own: the window takes the panel's keys before the console sees them, and lets
/// the rest through. Keys whose meaning depends on the line (Enter, Left, Right, Home, End, Del, Backspace, Esc, Tab)
/// reach the shell, which acts on a line with text and hands them back to the panel when it is empty
/// (<c>panel-key</c>). A click in the console works inside it ("console mode"); while a command runs, the console has
/// every key.
/// </summary>
public partial class MainWindow
{
    /// <summary>The user clicked into the console: keys are the console's until a panel is chosen again.</summary>
    private bool _consoleMode;

    /// <summary>Between a command's start and the next prompt: the console has every key.</summary>
    private bool _commandRunning;

    /// <summary>Keyboard popups open now (one can open as another closes).</summary>
    private int _openPopups;

    /// <summary>The last value given to the shell's panel-mode flag, so the flag file is written only on changes.</summary>
    private bool? _shellPanelMode;

    /// <summary>The user works in the console: it has the Win32 focus and no panel has the keys.</summary>
    private bool ConsoleHasKeys => TerminalHasFocus && FocusedPanel() is null;

    private void WireCommandLine(ShellHost host)
    {
        host.PanelKeyReturned += name => Dispatcher.BeginInvoke(() => OnPanelKeyReturned(name));
        host.CommandOutputStarted += (_, _, _) => Dispatcher.BeginInvoke(() =>
        {
            _commandRunning = true;
            UpdatePanelKeys();
        });
        host.StateChanged += state =>
        {
            if (state == ShellState.Idle)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _commandRunning = false;
                    UpdatePanelKeys();
                });
            }
        };
        Activated += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            // Back from another app: the keys go on where they were (WPF remembers no element to restore).
            if (!IsPageOpen && Keyboard.FocusedElement is null && !TerminalHasFocus && OwnedWindows.Count == 0)
            {
                PutFocusInTerminal();
            }

            UpdatePanelKeys();
        }, System.Windows.Threading.DispatcherPriority.Input);
        Deactivated += (_, _) => UpdatePanelKeys();
        PopupFocus.Opening += OnPopupOpening;
        PopupFocus.Closed += OnPopupClosed;
        Closed += (_, _) =>
        {
            PopupFocus.Opening -= OnPopupOpening;
            PopupFocus.Closed -= OnPopupClosed;
        };
    }

    /// <summary>A popup gets its keys through the window, so the Win32 focus leaves the console (issue #92).</summary>
    private void OnPopupOpening()
    {
        _openPopups++;
        if (TerminalHasFocus)
        {
            TakeFocusFromTerminal();
        }

        UpdatePanelKeys();
    }

    /// <summary>
    /// Back to the console, unless what the popup chose moved the focus on: a page, a dialog, another popup, a panel
    /// (which sends it back itself). Later than the continuation of the code that awaited the popup.
    /// </summary>
    private void OnPopupClosed()
    {
        _openPopups--;
        Dispatcher.BeginInvoke(() =>
        {
            if (_openPopups == 0 && !IsPageOpen && OwnedWindows.Count == 0 && !TerminalHasFocus && !_left.IsKeyboardFocused && !_right.IsKeyboardFocused)
            {
                PutFocusInTerminal();
            }

            UpdatePanelKeys();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Shows which panel has the keys, and tells the shell whether an empty line hands keys to a panel.</summary>
    private void UpdatePanelKeys()
    {
        var keys = FocusedPanel();
        _left.HasKeys = keys == _left;
        _right.HasKeys = keys == _right;

        var panelMode = !_consoleMode && AnyPanelVisible;
        if (_shell is not null && _shellPanelMode != panelMode)
        {
            _shellPanelMode = panelMode;
            _shell.Host.PanelMode = panelMode;
        }
    }

    /// <summary>
    /// A panel took the WPF focus (a click, or code focusing it): it gets the keys, and the Win32 focus goes back to
    /// the console. Skipped when something else took the focus meanwhile (a popup opening from the panel).
    /// </summary>
    private void OnPanelGotFocus(FilePanel panel)
    {
        Trace($"panel {panel.Side} got the WPF focus");
        _consoleMode = false;

        Dispatcher.BeginInvoke(() =>
        {
            if (Keyboard.FocusedElement == panel && !IsPageOpen)
            {
                PutFocusInTerminal();
            }

            UpdatePanelKeys();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// Keys that reach the shell while a panel has the keys: line editing with Shift selection, and the keys an empty
    /// line hands back to the panel.
    /// </summary>
    private static bool IsCommandLineKey(Key key, KeyModifiers modifiers) => modifiers switch
    {
        KeyModifiers.None => PanelKeyName(key) is not null,
        KeyModifiers.Shift => key is Key.Left or Key.Right or Key.Home or Key.End,
        KeyModifiers.Control | KeyModifiers.Shift => key is Key.Left or Key.Right,
        _ => false,
    };

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

    /// <summary>The keys an empty line hands back, by their names in the integration script's <c>panel-key</c>.</summary>
    private static readonly (string Name, Key Key)[] PanelKeys =
    [
        ("Enter", Key.Enter),
        ("Left", Key.Left),
        ("Right", Key.Right),
        ("Home", Key.Home),
        ("End", Key.End),
        ("Delete", Key.Delete),
        ("Backspace", Key.Back),
        ("Escape", Key.Escape),
        ("Tab", Key.Tab),
    ];

    private static string? PanelKeyName(Key key) => PanelKeys.FirstOrDefault(k => k.Key == key).Name;

    private static Key? PanelKey(string name) =>
        PanelKeys.FirstOrDefault(k => k.Name == name) is { Name: not null } found ? found.Key : null;
}
