using System.IO;
using System.Windows;
using HabilisTerminal.App.Hosting;
using HabilisTerminal.App.Layout;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Diagnostics;
using HabilisTerminal.Core.Panels;
using HabilisTerminal.Core.Settings;
using HabilisTerminal.Shell;
using HabilisTerminal.Shell.Integration;

namespace HabilisTerminal.App;

/// <summary>
/// Several consoles in the window (issue #119): a long command goes on in its tab while another one is in front. Panels
/// and keys belong to the tab in front; the others only collect their output and say when a command ends.
/// </summary>
public partial class MainWindow
{
    private readonly List<ConsoleTab> _tabs = [];
    private readonly ConsoleTabStrip _tabStrip = new();
    private ConsoleTab? _tab;
    private ConsoleHintButton? _hintButton;

    /// <summary>The panel configuration of the last console closed (issue #125); a new tab opens with it.</summary>
    private AppState _lastClosed = new();

    /// <summary>The last shell ended by itself: there is nothing left to ask about.</summary>
    private bool _lastShellExited;

    private ShellConnection? _shell => _tab?.Connection;

    private Microsoft.Terminal.Wpf.TerminalControl Terminal => _tab!.Terminal;

    private void InitTabs()
    {
        WorkspaceLayout.SetRole(_tabStrip, WorkspaceRole.ConsoleTabs);
        Workspace.Children.Add(_tabStrip);
        _tabStrip.TabChosen += ShowTab;
        _tabStrip.TabClosing += tab => CloseTab(tab);
        _tabStrip.NewTabRequested += NewTab;
        _hintButton = new ConsoleHintButton(this, Workspace, NewTab);
        Workspace.LayoutUpdated += (_, _) => UpdateHintButton();
    }

    private ConsoleTab CreateTab(string folder)
    {
        var pwsh = App.FindPwsh() ?? throw new InvalidOperationException("PowerShell 7 (pwsh.exe) was not found.");
        var host = new ShellHost(new ShellHostOptions(
            pwsh,
            ShellScripts.IntegrationScriptPath,
            folder,
            NullIfEmpty(_settings.ShellInitCommand),
            ExtraArguments: NullIfEmpty(_settings.ShellArgs),
            Integration: _settings.ShellIntegration));
        var tab = new ConsoleTab(host) { ColorIndex = Core.Panels.ConsoleTabRules.FreeColor(_tabs.Select(t => t.ColorIndex).ToList(), ConsoleTabStrip.ColorCount) };
        host.Exited += _ => Dispatcher.BeginInvoke(() => OnShellExited(tab));
        host.DirectoryChanged += path => Dispatcher.BeginInvoke(() =>
        {
            UpdateTabStrip();
            if (tab == _tab)
            {
                OnShellDirectoryChanged(path);
            }
        });
        host.FocusPanelRequested += () => Dispatcher.BeginInvoke(() =>
        {
            if (tab == _tab && !IsPageOpen && ConsoleHasKeys && IsShown(_active))
            {
                FocusActivePanel();
            }
        });
        host.RequestCompleted += (_, op, status) => Dispatcher.BeginInvoke(() =>
        {
            if (tab == _tab)
            {
                OnShellRequestCompleted(op, status);
            }
        });
        host.AddressEntered += address => Dispatcher.BeginInvoke(() =>
        {
            if (tab == _tab)
            {
                OnPluginAddress(address);
            }
        });
        host.CommandLineAccepted += line => Dispatcher.BeginInvoke(() => tab.CommandLine = line);
        host.CommandFinished += code => Dispatcher.BeginInvoke(() => tab.LastExitCode = code);
        host.AddressPrefixes = _plugins.Prefixes();
        host.AddressCommands = _plugins.AddressCommands();
        WireOverflow(tab);
        WireCommandLine(tab);

        WorkspaceLayout.SetRole(tab.Terminal, WorkspaceRole.Terminal);
        Workspace.Children.Insert(0, tab.Terminal);
        _tabs.Add(tab);
        return tab;
    }

    /// <summary>Theme, then the connection, which starts the shell; the control must be in the window by now.</summary>
    private void StartTab(ConsoleTab tab)
    {
        ApplyTerminalTheme(tab, _themes.Current);
        tab.Terminal.Connection = tab.Connection;

        // The control sizes the connection only when its own size changes later, so the shell started at the
        // connection's default 120x30 and kept it until the layout changed (found in issue #82). Tell it the real size.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (tab.Terminal.Rows > 0 && tab.Terminal.Columns > 0)
            {
                tab.Connection.Resize((uint)tab.Terminal.Rows, (uint)tab.Terminal.Columns);
            }

            if (tab == _tab && !IsPageOpen)
            {
                PutFocusInTerminal();
            }
        });
    }

    /// <summary>Everything that belongs to one panel: its events go to the window, whichever tab it is in.</summary>
    private void WirePanel(FilePanel panel)
    {
        panel.Navigated += OnPanelNavigated;
        panel.ExecuteRequested += OnPanelExecuteRequested;
        panel.OpensAsFolder = _plugins.OpensAsFolder;
        panel.FileAsFolderRequested += (p, item, anyFormat) => Fire(OpenFileAsFolderAsync(p, item, anyFormat));
        panel.ShellMenuRequested += OnPanelShellMenuRequested;
        panel.Changed += OnPanelChanged;
        panel.GotKeyboardFocus += (_, _) => OnPanelFocused(panel);
        panel.GotKeyboardFocus += (_, _) =>
        {
            CollapseConsole(stopGrowing: true); // issue #82
            OnPanelGotFocus(panel); // issue #90: the panel gets the keys, the console keeps the Win32 focus
        };
        panel.PluginLocationChanged += (_, location) => _history.Add(location);
        panel.PluginEntryActivated += (p, entry) => Fire(PluginViewAsync(p, entry));
        panel.WaitToasts = () => Toasts;
    }

    /// <summary>The panels of every tab: those in front are the window's, as Ctrl+U swaps them.</summary>
    private IEnumerable<FilePanel> AllPanels()
    {
        yield return _left;
        yield return _right;
        foreach (var tab in _tabs.Where(t => t != _tab))
        {
            yield return tab.Left;
            yield return tab.Right;
        }
    }

    /// <summary>A new tab's panels, set up from a saved configuration; the folders open once they are in the window.</summary>
    private void CreatePanels(ConsoleTab tab, AppState configuration)
    {
        tab.Left = new FilePanel("Left");
        tab.Right = new FilePanel("Right");
        WorkspaceLayout.SetRole(tab.Left, WorkspaceRole.LeftPanel);
        WorkspaceLayout.SetRole(tab.Right, WorkspaceRole.RightPanel);
        WirePanel(tab.Left);
        WirePanel(tab.Right);
        RestorePanel(tab.Left, configuration.Left);
        RestorePanel(tab.Right, configuration.Right);
        tab.LeftVisible = configuration.Left.Visible;
        tab.RightVisible = configuration.Right.Visible;
        var visible = (configuration.Left.Visible, configuration.Right.Visible);
        tab.Active = PanelVisibilityRule.ActiveIsLeft(configuration.ActivePanel != "Right", visible, visible) ? tab.Left : tab.Right;
        tab.TerminalRows = configuration.TerminalRows ?? Workspace.TerminalRows;
        tab.SplitRatio = configuration.SplitRatio ?? Workspace.SplitRatio;
        tab.SinglePanelRatio = configuration.SinglePanelRatio ?? Workspace.SinglePanelRatio;
    }

    /// <summary>The tab in front keeps its panels and layout in the window's fields; this gives them back to the tab.</summary>
    private void SavePanels(ConsoleTab tab)
    {
        tab.Left = _left;
        tab.Right = _right;
        tab.Active = _active;
        tab.LeftVisible = Workspace.LeftVisible;
        tab.RightVisible = Workspace.RightVisible;
        tab.RestoreVisibility = _restoreVisibility;
        tab.Reveal = _reveal;
        tab.RevealFromTerminal = _revealFromTerminal;
        tab.TerminalRows = Workspace.TerminalRows;
        tab.SplitRatio = Workspace.SplitRatio;
        tab.SinglePanelRatio = Workspace.SinglePanelRatio;
    }

    private void LoadPanels(ConsoleTab tab)
    {
        if (_left != tab.Left || _right != tab.Right)
        {
            _left.HasKeys = false;
            _right.HasKeys = false;
            Workspace.Children.Remove(_left);
            Workspace.Children.Remove(_right);
            _left = tab.Left;
            _right = tab.Right;
            Workspace.Children.Add(_left);
            Workspace.Children.Add(_right);
        }

        _restoreVisibility = tab.RestoreVisibility;
        _reveal = tab.Reveal;
        _revealFromTerminal = tab.RevealFromTerminal;
        Workspace.LeftVisible = tab.LeftVisible;
        Workspace.RightVisible = tab.RightVisible;
        Workspace.TerminalRows = tab.TerminalRows;
        Workspace.SplitRatio = tab.SplitRatio;
        Workspace.SinglePanelRatio = tab.SinglePanelRatio;
        Activate(tab.Active);
    }

    /// <summary>A tab's panel configuration, as it would be saved.</summary>
    private static AppState Configuration(ConsoleTab tab) => new()
    {
        Left = Snapshot(tab.Left, tab.LeftVisible),
        Right = Snapshot(tab.Right, tab.RightVisible),
        ActivePanel = tab.Active == tab.Right ? "Right" : "Left",
        TerminalRows = tab.TerminalRows,
        SplitRatio = tab.SplitRatio,
        SinglePanelRatio = tab.SinglePanelRatio,
    };

    /// <summary>Ctrl+Shift+T: a new shell with the panels of the last console closed; never closed one: like the one in front.</summary>
    private void NewTab()
    {
        using var measure = Perf.Measure("tab.new");
        if (IsPageOpen)
        {
            return;
        }

        if (_lastClosed.Left.Path is null && _tab is { } current)
        {
            SavePanels(current);
            _lastClosed = Configuration(current);
        }

        var configuration = _lastClosed;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var activeIsRight = configuration.ActivePanel == "Right" && configuration.Right.Visible;
        var tab = CreateTab(StartPath(activeIsRight ? configuration.Right : configuration.Left, home));
        CreatePanels(tab, configuration);
        tab.Terminal.Visibility = Visibility.Hidden;
        void OnTerminalLoaded(object sender, RoutedEventArgs e)
        {
            tab.Terminal.Loaded -= OnTerminalLoaded;
            StartTab(tab);
        }

        tab.Terminal.Loaded += OnTerminalLoaded;
        ShowTab(tab);
        _ = tab.Left.NavigateAsync(StartPath(configuration.Left, home), configuration.Left.CurrentName);
        _ = tab.Right.NavigateAsync(StartPath(configuration.Right, home), configuration.Right.CurrentName);
    }

    /// <summary>Ctrl+Tab and Ctrl+Shift+Tab; false with a single console, so that the key goes on to it.</summary>
    private bool NextTab(int direction)
    {
        if (_tabs.Count < 2 || _tab is null)
        {
            return false;
        }

        ShowTab(_tabs[Core.Panels.ConsoleTabRules.Next(_tabs.IndexOf(_tab), _tabs.Count, direction)]);
        return true;
    }

    /// <summary>Brings a tab to the front, with its own panels and layout (issue #125).</summary>
    private void ShowTab(ConsoleTab tab)
    {
        using var measure = Perf.Measure("tab.show");
        var previous = _tab;
        ReleaseFrame();
        if (tab != previous && previous is not null)
        {
            previous.OverlayRows = Workspace.OverlayRows;
            SavePanels(previous);
        }

        _tab = tab;
        LoadPanels(tab);
        tab.Terminal.Visibility = Visibility.Visible;
        if (previous is not null && previous != tab)
        {
            previous.Terminal.Visibility = Visibility.Hidden;
        }

        Workspace.OverlayRows = tab.OverlayRows;

        // Hidden to visible starts no layout, and the layout skipped this console while it was hidden: its window would
        // stay where it was, over the tabs, and at the old size if the window has changed meanwhile.
        tab.Terminal.InvalidateMeasure();
        Workspace.InvalidateMeasure();
        Workspace.InvalidateArrange();
        var frame = tab.ColorIndex == 0 ? null : ConsoleTabStrip.ColorOf(tab.ColorIndex);
        _left.FrameBrush = frame;
        _right.FrameBrush = frame;
        if (tab.Mark is ConsoleTabMark.Succeeded or ConsoleTabMark.Failed)
        {
            tab.Mark = ConsoleTabMark.None;
        }

        Toasts.Dismiss(FinishedCardKey(tab));
        UpdatePanelsShown();
        UpdatePanelKeys();
        UpdateTabStrip();
        if (tab.CommandRunning)
        {
            GrowConsole(tab.OverflowPosted.Rows, tab.OverflowPosted.Alternate);
        }

        // The control may not have its window yet; StartTab then gives it the focus.
        Dispatcher.BeginInvoke(() =>
        {
            if (tab == _tab && !IsPageOpen)
            {
                PutFocusInTerminal();
                UpdatePanelKeys();
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Ctrl+Shift+W and the tab's ×. The last console is closed by leaving the app, not from here.</summary>
    private void CloseTab(ConsoleTab tab, bool ask = true)
    {
        if (_tabs.Count < 2 || !_tabs.Contains(tab))
        {
            return;
        }

        if (ask && Core.Panels.ConsoleTabRules.AskBeforeClosing(tab.CommandRunning ? 1 : 0))
        {
            var close = ThemedDialog.Show(this, "Close console?", LabelBlock($"{tab.Title} is still running. Closing the console stops it."),
                [new DialogButton<bool>("_Close", true, IsDefault: true), new DialogButton<bool>("Cancel", false, IsCancel: true)], false);
            PutFocusInTerminal();
            if (!close)
            {
                return;
            }
        }

        if (tab == _tab)
        {
            SavePanels(tab);
        }

        _lastClosed = Configuration(tab);
        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        Toasts.Dismiss(FinishedCardKey(tab));
        if (tab == _tab)
        {
            _tab = null;
            ShowTab(_tabs[Core.Panels.ConsoleTabRules.AfterClosing(index, _tabs.Count)]);
        }

        tab.Left.Release();
        tab.Right.Release();
        Workspace.Children.Remove(tab.Terminal);
        tab.Dispose();
        (FindHwndHost(tab.Terminal) as IDisposable)?.Dispose();
        UpdateTabStrip();
    }

    private void OnShellExited(ConsoleTab tab)
    {
        if (_tabs.Count < 2)
        {
            _lastShellExited = true;
            Close();
        }
        else
        {
            CloseTab(tab, ask: false);
        }
    }

    /// <summary>A command ended. In a tab not in front, the mark and a card say so.</summary>
    private void OnTabCommandEnded(ConsoleTab tab)
    {
        if (tab == _tab)
        {
            tab.Mark = ConsoleTabMark.None;
            UpdateTabStrip();
            return;
        }

        tab.Mark = tab.LastExitCode is null or 0 ? ConsoleTabMark.Succeeded : ConsoleTabMark.Failed;
        UpdateTabStrip();
        var line = (tab.CommandLine ?? "The command").Trim();
        var result = tab.LastExitCode is { } code ? $"Exit code {code}." : "Finished.";
        Toasts.Notify(FinishedCardKey(tab), $"{Shorten(line)} finished", result, ("Show", () =>
        {
            if (_tabs.Contains(tab))
            {
                ShowTab(tab);
            }
        }));
    }

    private static string Shorten(string line) => line.Length <= 60 ? line : line[..59] + "…";

    private static string FinishedCardKey(ConsoleTab tab) => $"console-finished-{tab.GetHashCode()}";

    /// <summary>Only with a single console: then the strip is not there to show that more are possible. Not over a full-screen program.</summary>
    private void UpdateHintButton()
    {
        if (_tabs.Count != 1 || _tab is not { } tab || IsPageOpen || !IsLoaded || tab.OverflowPosted.Alternate || tab.Terminal.ActualWidth <= 0)
        {
            _hintButton?.Update(null);
            return;
        }

        var corner = tab.Terminal.TranslatePoint(new Point(tab.Terminal.ActualWidth, tab.Terminal.ActualHeight), Workspace);
        _hintButton?.Update(new Rect(0, 0, corner.X, corner.Y));
    }

    private void UpdateTabStrip()
    {
        Workspace.ShowsTabStrip = _tabs.Count > 1;
        if (_tabs.Count > 1)
        {
            _tabStrip.Update(_tabs, _tab);
        }
        else
        {
            _tabStrip.Clear();
        }
    }

    /// <summary>Closing the window stops every running command, so it asks first.</summary>
    private bool ConfirmCloseConsoles()
    {
        var running = _tabs.Count(t => t.CommandRunning);
        if (_lastShellExited || !Core.Panels.ConsoleTabRules.AskBeforeClosing(running))
        {
            return true;
        }

        var text = running == 1 ? "A command is still running. Closing the window stops it." : $"{running} commands are still running. Closing the window stops them.";
        var close = ThemedDialog.Show(this, "Close the window?", LabelBlock(text),
            [new DialogButton<bool>("_Close", true, IsDefault: true), new DialogButton<bool>("Cancel", false, IsCancel: true)], false);
        PutFocusInTerminal();
        return close;
    }
}
