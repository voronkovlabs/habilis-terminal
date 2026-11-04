using System.IO;
using System.Windows;
using HabilisTerminal.App.Hosting;
using HabilisTerminal.App.Layout;
using HabilisTerminal.App.Widgets;
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
        var tab = new ConsoleTab(host);
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

    /// <summary>Ctrl+Shift+T: a new shell in the active panel's folder.</summary>
    private void NewTab()
    {
        if (IsPageOpen)
        {
            return;
        }

        var tab = CreateTab(StartFolder());
        tab.Terminal.Visibility = Visibility.Hidden;
        void OnTerminalLoaded(object sender, RoutedEventArgs e)
        {
            tab.Terminal.Loaded -= OnTerminalLoaded;
            StartTab(tab);
        }

        tab.Terminal.Loaded += OnTerminalLoaded;
        ShowTab(tab);
    }

    private string StartFolder()
    {
        var folder = _active.LocalPath;
        return folder.Length > 0 && Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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

    /// <summary>Brings a tab to the front. The active panel goes to its folder, unless it shows a plugin.</summary>
    private void ShowTab(ConsoleTab tab)
    {
        var previous = _tab;
        if (tab != previous && previous is not null)
        {
            previous.OverlayRows = Workspace.OverlayRows;
        }

        _tab = tab;
        tab.Terminal.Visibility = Visibility.Visible;
        if (previous is not null && previous != tab)
        {
            previous.Terminal.Visibility = Visibility.Hidden;
        }

        Workspace.OverlayRows = tab.OverlayRows;
        if (tab.Mark is ConsoleTabMark.Succeeded or ConsoleTabMark.Failed)
        {
            tab.Mark = ConsoleTabMark.None;
        }

        Toasts.Dismiss(FinishedCardKey(tab));
        ReleaseFrame();
        UpdatePanelsShown();
        UpdatePanelKeys();
        UpdateTabStrip();
        if (tab.CommandRunning)
        {
            GrowConsole(tab.OverflowPosted.Rows, tab.OverflowPosted.Alternate);
        }

        if (!_active.IsPlugin && tab.Host.CurrentDirectory is { Length: > 0 } folder &&
            !string.Equals(folder, _active.LocalPath, StringComparison.OrdinalIgnoreCase))
        {
            _ = _active.NavigateAsync(folder);
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

        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        Toasts.Dismiss(FinishedCardKey(tab));
        if (tab == _tab)
        {
            _tab = null;
            ShowTab(_tabs[Core.Panels.ConsoleTabRules.AfterClosing(index, _tabs.Count)]);
        }

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

    private void UpdateTabStrip()
    {
        Workspace.ShowsTabStrip = _tabs.Count > 1;
        if (_tabs.Count > 1)
        {
            _tabStrip.Update(_tabs, _tab);
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
