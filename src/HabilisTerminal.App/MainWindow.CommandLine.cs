using Avalonia.Input;
using HabilisTerminal.App.Hosting;
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

    /// <summary>Between a command's start and the next prompt, in the tab in front: the console has every key.</summary>
    private bool _commandRunning => _tab?.CommandRunning == true;

    /// <summary>
    /// A running command has every key, but the panel keeps its look for this long: otherwise each short command makes
    /// the frame and the cursor bar blink (issue #118).
    /// </summary>
    private static readonly TimeSpan FrameHoldTime = TimeSpan.FromSeconds(2);

    private Avalonia.Threading.DispatcherTimer? _frameHold;

    /// <summary>Keyboard popups open now (one can open as another closes).</summary>
    private int _openPopups;

    /// <summary>The shell runs the integration script, so the line's state is known (shell.integration, read at start).</summary>
    /// <summary>
    /// The console's shell has our integration: pwsh or zsh, with shell.integration on. A shell without it (bash on a
    /// runner, issue #268) gets keys as they are: nothing else would read the private ones.
    /// </summary>
    private bool ShellIntegrated => _settings.ShellIntegration && _shell?.Host.Kind is Shell.Integration.ShellKind.Pwsh or Shell.Integration.ShellKind.Zsh;

    /// <summary>The user works in the console: it has the Win32 focus and no panel has the keys.</summary>
    private bool ConsoleHasKeys => TerminalHasFocus && FocusedPanel() is null;

    private void WireCommandLine(ConsoleTab tab)
    {
        var host = tab.Host;
        host.PanelKeyReturned += name => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (tab == _tab)
            {
                OnPanelKeyReturned(name);
            }
        });
        host.CommandOutputStarted += (_, _, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (tab == _tab)
            {
                HoldFrame();
            }

            tab.CommandRunning = true;
            tab.CommandStartedAt = DateTime.UtcNow;
            tab.Mark = ConsoleTabMark.Running;
            if (tab == _tab)
            {
                UpdatePanelKeys();
            }

            UpdateTabStrip();
        });
        host.StateChanged += state =>
        {
            if (state == ShellState.Idle)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var ended = tab.CommandRunning;
                    tab.CommandRunning = false;
                    if (ended)
                    {
                        tab.LastDuration = DateTime.UtcNow - tab.CommandStartedAt;
                    }

                    if (tab == _tab)
                    {
                        UpdatePanelKeys();
                        ReleaseFrame();
                    }

                    if (ended)
                    {
                        OnTabCommandEnded(tab);
                    }
                });
            }
        };
    }

    private void WireWindowKeys()
    {
        Activated += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Back from another app: the keys go on where they were (WPF remembers no element to restore).
            if (!IsPageOpen && FocusManager?.GetFocusedElement() is null && !TerminalHasFocus && OwnedWindows.Count == 0)
            {
                PutFocusInTerminal();
            }

            UpdatePanelKeys();
        }, Avalonia.Threading.DispatcherPriority.Input);
        Deactivated += (_, _) => UpdatePanelKeys();
        PopupFocus.Opening += OnPopupOpening;
        PopupFocus.Closed += OnPopupClosed;
        Closed += (_, _) =>
        {
            PopupFocus.Opening -= OnPopupOpening;
            PopupFocus.Closed -= OnPopupClosed;
        };
    }

    private void HoldFrame()
    {
        if (FocusedPanel() is not { } panel)
        {
            return;
        }

        panel.HoldsFrame = true;
        if (_frameHold is null)
        {
            _frameHold = new Avalonia.Threading.DispatcherTimer { Interval = FrameHoldTime };
            _frameHold.Tick += (_, _) => ReleaseFrame();
        }

        _frameHold.Stop();
        _frameHold.Start();
    }

    private void ReleaseFrame()
    {
        _frameHold?.Stop();
        _left.HoldsFrame = false;
        _right.HoldsFrame = false;
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
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_openPopups == 0 && !IsPageOpen && OwnedWindows.Count == 0 && !TerminalHasFocus && !_left.IsKeyboardFocusWithin && !_right.IsKeyboardFocusWithin)
            {
                PutFocusInTerminal();
            }

            UpdatePanelKeys();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Shows which panel has the keys, and tells the shell whether an empty line hands keys to a panel.</summary>
    private void UpdatePanelKeys()
    {
        var keys = FocusedPanel();
        _left.HasKeys = keys == _left;
        _right.HasKeys = keys == _right;

        var panelMode = !_consoleMode && AnyPanelVisible;
        if (_tab is { } tab && tab.ShellPanelMode != panelMode)
        {
            tab.ShellPanelMode = panelMode;
            tab.Host.PanelMode = panelMode;
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

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (FocusManager?.GetFocusedElement() == panel && !IsPageOpen)
            {
                PutFocusInTerminal();
            }

            UpdatePanelKeys();
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// While quick search is open, text goes to it and not to the console, whatever produced it (issue #107):
    /// <paramref name="typed"/> is '\0' when only asking, else a typed character.
    /// </summary>
    private bool CaptureQuickSearchText(char typed)
    {
        if (FocusedPanel() is not { HasKeys: true, IsQuickSearchActive: true } panel)
        {
            return false;
        }

        if (typed != '\0' && !char.IsControl(typed))
        {
            panel.AppendQuickSearch(typed.ToString());
        }

        return true;
    }

    /// <summary>
    /// Keys that reach the shell while a panel has the keys: line editing with Shift selection, and the keys an empty
    /// line hands back to the panel.
    /// </summary>
    private static bool IsCommandLineKey(Key key, KeyModifiers modifiers) => modifiers switch
    {
        KeyModifiers.None => PanelKeyName(key) is not null && GrayKeyName(key, modifiers) is null,
        KeyModifiers.Shift => key is Key.Left or Key.Right or Key.Home or Key.End,
        KeyModifiers.Control | KeyModifiers.Shift => key is Key.Left or Key.Right,
        _ => false,
    };

    /// <summary>
    /// Keys that are always the panel's: while the console is grown over the panels (issue #82) they collapse it first,
    /// so nothing acts on a panel that cannot be seen.
    /// </summary>
    private static bool IsPanelOnlyKey(Key key) =>
        key is >= Key.F1 and <= Key.F12 or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Insert;

    /// <summary>The command line was empty: the key acts in the panel after all.</summary>
    private void OnPanelKeyReturned(string name)
    {
        if (IsPageOpen || PanelKey(name) is not { } key)
        {
            return;
        }

        // The console grown over the panels hides them, so these keys only collapse it (issue #82): Esc by design, and
        // Enter and Del must not run or delete an item nobody can see. The others collapse it and act.
        if (OverflowExpanded)
        {
            CollapseConsole(stopGrowing: false);
            if (key is Key.Escape or Key.Enter or Key.Delete)
            {
                return;
            }
        }

        if (FocusedPanel() is not { } panel || HandleKeyOverHiddenList(panel, key, KeyModifiers.None))
        {
            return;
        }

        if (!HandlePluginKey(panel, key, KeyModifiers.None) && !HandleWindowKey(panel, key, KeyModifiers.None) &&
            !panel.HandleKey(key, KeyModifiers.None) && key == Key.Escape)
        {
            // Esc on an empty line that the panel has no use for (no quick filter, no plugin cursor): the keys go to
            // the command line, and the next Esc there brings them back (issue #290).
            FocusTerminal();
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
        ("Add", Key.Add),
        ("Subtract", Key.Subtract),
        ("Multiply", Key.Multiply),
    ];

    private static string? PanelKeyName(Key key) => PanelKeys.FirstOrDefault(k => k.Key == key).Name;

    /// <summary>
    /// Gray +, - and * select files on an empty line and are typed on a line with text, as in Far. PSReadLine cannot
    /// tell them from the main keyboard's, so they are sent as keys of their own (<see cref="ShellHost.SendGrayKey"/>).
    /// </summary>
    private static string? GrayKeyName(Key key, KeyModifiers modifiers) => modifiers != KeyModifiers.None ? null : key switch
    {
        Key.Add => "Add",
        Key.Subtract => "Subtract",
        Key.Multiply => "Multiply",
        _ => null,
    };

    private static Key? PanelKey(string name) =>
        PanelKeys.FirstOrDefault(k => k.Name == name) is { Name: not null } found ? found.Key : null;
}
