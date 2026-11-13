using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HabilisTerminal.App.Hosting;
using HabilisTerminal.App.Layout;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Theming;
using HabilisTerminal.Core.Panels;

namespace HabilisTerminal.App;

/// <summary>
/// The window: two file panels and the console (SPEC §3), focus and visibility (SPEC §4), shell synchronisation
/// (SPEC §7). Panel commands live in MainWindow.Commands.cs.
/// </summary>
/// <remarks>
/// On Avalonia (issue #253) the console is a control of the window, not a native child window: it holds the focus while
/// a panel "has the keys" (issue #90), and the window's tunnelling key handler routes the keys, as the WPF window's
/// message hook did.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed when the window closes.")]
public partial class MainWindow : Window
{
    private readonly ThemeService _themes = new();
    private readonly FolderHistory _history = new();
    private Core.Plugins.PluginHotkeys _pluginHotkeys = new();
    private readonly Dictionary<string, string> _lastPathPerDrive = new(StringComparer.OrdinalIgnoreCase);
    private FilePanel _left = new("Left");
    private FilePanel _right = new("Right");
    private FilePanel _active;
    private (bool Left, bool Right) _restoreVisibility = (true, true);
    private HiddenPanelReveal _reveal = new();
    private bool _revealFromTerminal;
    private DispatcherTimer? _themeReloadTimer;
    private readonly Widgets.TitleBar _titleBar;

    // The window's tree (was MainWindow.xaml): the title bar and, under it, the workspace with a page host over it.
    private readonly DockPanel Root = new() { LastChildFill = true };
    private readonly WorkspaceLayout Workspace = new();

    /// <summary>
    /// Full-window pages (viewer, editor, settings). The workspace is hidden, not collapsed, while a page is shown, so the
    /// console keeps its size and the shell is not resized.
    /// </summary>
    private readonly ContentControl PageHost = new() { IsVisible = false, Focusable = false };

    public MainWindow()
    {
        Title = "Habilis Terminal";
        Width = 1200;
        Height = 800;

        // A first start on a small screen (a 1024×768 Mac, #259): the window fits, centred.
        if (Screens.Primary is { } screen)
        {
            var area = screen.WorkingArea.Size.ToSize(screen.Scaling);
            Width = Math.Min(Width, area.Width - 32);
            Height = Math.Min(Height, area.Height - 32);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        UseLayoutRounding = true;
        var grid = new Grid();
        grid.Children.Add(Workspace);
        grid.Children.Add(PageHost);
        var parking = new Canvas { IsHitTestVisible = false };
        grid.Children.Add(parking);
        Widgets.PopupParking.Register(parking);
        Root.Children.Add(grid);
        Content = Root;

        Widgets.TitleBar.Install(this);
        _titleBar = new Widgets.TitleBar(this, MenuTitles);
        _titleBar.MenuRequested += group =>
        {
            if (!IsPageOpen)
            {
                Fire(ShowTopMenuAsync(group));
            }
        };
        _titleBar.FeedbackRequested += ShowFeedback;
        _titleBar.CommandBoxRequested += () => Fire(ShowCommandLineAsync());
        DockPanel.SetDock(_titleBar, Dock.Top);
        Root.Children.Insert(0, _titleBar);

        var state = App.State?.Load() ?? new Core.Settings.AppState();
        InstallZoom(state);
        RestoreWindowPlacement(state);
        _themes.Changed += OnThemeChanged;
        ApplySettings(_settings);

        _active = _left;
        _left.IsActive = true;
        WorkspaceLayout.SetRole(_left, WorkspaceRole.LeftPanel);
        WorkspaceLayout.SetRole(_right, WorkspaceRole.RightPanel);
        Workspace.Children.Add(_left);
        Workspace.Children.Add(_right);
        WirePanel(_left);
        WirePanel(_right);
        LoadPlugins();
        RestoreLayout(state);
        _lastClosed = state;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var leftStart = StartPath(state.Left, home);
        var rightStart = StartPath(state.Right, home);
        InitTabs();
        WireWindowKeys();
        var first = CreateTab(_active == _right ? rightStart : leftStart);
        SavePanels(first);
        ShowTab(first);
        UpdatePanelsShown(); // the layout was restored before the shell existed

        Loaded += async (_, _) =>
        {
            OnLoaded();
            ShowSettingsErrors();
            ShowThemeError();
            if (App.Settings is { } settings)
            {
                settings.Changed += OnSettingsFileChanged;
                settings.Watch();
            }

            Reporting.PerfDiagnostics.RecordStartupMilestone("startup.window-loaded");
            StartStateTimer();
            App.Updates.UpdateReady += OnUpdateReady;
            await _left.NavigateAsync(leftStart, state.Left.CurrentName);
            await _right.NavigateAsync(rightStart, state.Right.CurrentName);
        };
        Opened += (_, _) =>
        {
            Reporting.PerfDiagnostics.RecordStartupMilestone("startup.first-render");
            UpdateTitleBar();

            // Windows mode, high contrast and accent changes repaint everything live (SPEC §10); elsewhere Avalonia tells.
            if (OperatingSystem.IsWindows())
            {
                Win32Properties.AddWndProcHookCallback(this, OnWindowMessage);
            }
            else if (Application.Current?.PlatformSettings is { } platform)
            {
                platform.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(_themes.Reload);
            }
        };

        // Every key of the window passes here first, as the console's keys passed the WPF window's message hook: the
        // panels, pages and window commands take theirs, the rest goes on to the focused element, the console mostly.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnWindowTextInput, RoutingStrategies.Tunnel);
        Closing += (_, e) =>
        {
            // Unsaved edits are never lost silently, whatever closes the window (F10, Alt+F4, the close button).
            if (PageHost.Content is Editing.EditorPage editor && !editor.ConfirmLeave())
            {
                e.Cancel = true;
                return;
            }

            if (!ConfirmCloseConsoles())
            {
                e.Cancel = true;
                return;
            }

            SaveState();
        };
        Closed += (_, _) =>
        {
            _stateTimer?.Stop();
            App.Updates.UpdateReady -= OnUpdateReady;
            if (App.Settings is { } settings)
            {
                settings.Changed -= OnSettingsFileChanged;
            }

            Reporting.PerfDiagnostics.Stop();
            foreach (var tab in _tabs)
            {
                tab.Dispose();
            }

            _themes.Dispose();
        };
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private bool AnyPanelVisible => Workspace.LeftVisible || Workspace.RightVisible;

    private bool PanelHasFocus => FocusedPanel() is not null;

    private FilePanel Other(FilePanel panel) => panel == _left ? _right : _left;

    private IntPtr OnWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
        var themeRelated = message == WM_DWMCOLORIZATIONCOLORCHANGED ||
            (message == WM_SETTINGCHANGE && (lParam == IntPtr.Zero || Marshal.PtrToStringUni(lParam) is "ImmersiveColorSet" or "WindowsThemeElement"));
        if (themeRelated)
        {
            _themeReloadTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
            {
                _themeReloadTimer!.Stop();
                _themes.Reload();
            });
            _themeReloadTimer.Stop();
            _themeReloadTimer.Start();
        }

        return Widgets.TitleBar.OnWindowMessage(this, hwnd, message, wParam, lParam, ref handled);
    }

    private void OnThemeChanged(Themes.ColorTheme theme)
    {
        using var measure = Core.Diagnostics.Perf.Measure("theme.apply");
        Background = PanelPalette.Background;
        QuickView.TextTheme = theme;
        IconProvider.OnColorThemeChanged();
        foreach (var panel in AllPanels())
        {
            panel.ApplyTheme();
        }

        if (PageHost?.Content is Editing.EditorPage editor)
        {
            editor.ApplyTheme(theme);
        }
        else if (PageHost?.Content is Viewing.ViewerPage viewer)
        {
            viewer.ApplyTheme(theme);
        }

        if (IsLoaded)
        {
            ApplyTerminalTheme(theme);
            UpdateTitleBar();
            UpdateTabStrip();
        }
    }

    private void ApplyTerminalTheme(Themes.ColorTheme theme)
    {
        foreach (var tab in _tabs)
        {
            ApplyTerminalTheme(tab, theme);
        }
    }

    private void ApplyTerminalTheme(ConsoleTab tab, Themes.ColorTheme theme) =>
        tab.Terminal.SetTheme(ThemeService.ToTerminalPalette(theme), TerminalFontFamily, TerminalEmSize);

    private void UpdateTitleBar()
    {
        RequestedThemeVariant = _themes.Current.IsLight ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        Platform.Desktop.Current.SetDarkFrame(this, !_themes.Current.IsLight);
    }

    private void OnLoaded()
    {
        UpdateCellSize();
        StartTab(_tab!);
        FocusActivePanel();
    }

    /// <summary>The window's key handler, ahead of the focused element (see the constructor).</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        if (OnGlobalKey(e.Key, e.KeyModifiers & (KeyModifiers.Alt | KeyModifiers.Control | KeyModifiers.Shift)))
        {
            e.Handled = true;
        }
    }

    /// <summary>Printable characters typed in a panel go to the command line, or to quick search while it is open (SPEC §4).</summary>
    private void OnWindowTextInput(object? sender, TextInputEventArgs e)
    {
        if (!TerminalHasFocus || FocusedPanel() is not { } panel || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl))
        {
            return;
        }

        e.Handled = true;
        if (panel.IsQuickSearchActive)
        {
            panel.AppendQuickSearch(e.Text);
            return;
        }

        // As in Far, the keys stay with the panel (issue #85).
        _shell?.WriteInput(e.Text);
    }

    // ----- Visibility and focus -----

    private void TogglePanels()
    {
        if (AnyPanelVisible)
        {
            _restoreVisibility = (Workspace.LeftVisible, Workspace.RightVisible);
            SetPanelVisibility(false, false, keepReveal: true);
        }
        else
        {
            SetPanelVisibility(_restoreVisibility.Left, _restoreVisibility.Right, keepReveal: true);
        }
    }

    /// <summary>A panel shown: Esc on an empty line inside the console goes back to it (see the integration script).</summary>
    private void UpdatePanelsShown()
    {
        if (_shell is not null)
        {
            _shell.Host.PanelsShown = AnyPanelVisible;
        }
    }

    /// <param name="keepReveal">
    /// False for layout changes that make a panel shown by Ctrl+L / Ctrl+Q part of the layout (issue #38); Ctrl+O and
    /// the reveal itself keep it.
    /// </param>
    private void SetPanelVisibility(bool left, bool right, bool keepReveal = false)
    {
        CollapseConsole(stopGrowing: false); // any layout change ends the console grown over the panels (issue #82)
        (bool Left, bool Right) before = (Workspace.LeftVisible, Workspace.RightVisible);
        if (!keepReveal)
        {
            _reveal.Forget();
        }

        if ((before.Left || before.Right) && !left && !right)
        {
            _restoreVisibility = before;
        }

        Workspace.LeftVisible = left;
        Workspace.RightVisible = right;
        UpdatePanelsShown();
        UpdatePanelKeys();

        if (!AnyPanelVisible)
        {
            FocusTerminal();
            return;
        }

        // Every path that shows or hides a panel ends here, so the rule of issue #35 lives in one place.
        var active = PanelVisibilityRule.ActiveIsLeft(_active == _left, before, (left, right)) ? _left : _right;
        if (active != _active)
        {
            Activate(active);
            SyncShellDirectory();
        }

        if (PanelVisibilityRule.FocusesPanel(before, (left, right), ConsoleHasKeys))
        {
            FocusActivePanel();
        }
    }

    /// <summary>
    /// The panel that has the keys (issue #90): the active panel while the console holds the focus, outside console
    /// mode and while no command runs. Otherwise a panel that holds the focus for a moment (a click, before the focus
    /// goes back to the console).
    /// </summary>
    private FilePanel? FocusedPanel()
    {
        if (TerminalHasFocus)
        {
            return !_consoleMode && !_commandRunning && IsShown(_active) ? _active : null;
        }

        return _left.IsKeyboardFocusWithin ? _left : _right.IsKeyboardFocusWithin ? _right : null;
    }

    private bool IsShown(FilePanel? panel) =>
        panel == _left ? Workspace.LeftVisible : panel == _right && Workspace.RightVisible;

    private void Activate(FilePanel panel)
    {
        _active = panel;
        _left.IsActive = panel == _left;
        _right.IsActive = panel == _right;
        UpdatePanelKeys();
    }

    private void OnPanelFocused(FilePanel panel)
    {
        if (panel != _active)
        {
            Activate(panel);
            SyncShellDirectory();
        }
    }

    /// <summary>The active panel gets the keys (issue #90); the focus goes to the console, as always.</summary>
    private void FocusActivePanel()
    {
        _consoleMode = false;

        // The panel may only become visible after the next layout pass.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!IsPageOpen)
            {
                PutFocusInTerminal();
            }

            UpdatePanelKeys();
            Trace($"keys to panel {_active.Side}");
        }, DispatcherPriority.Input);
    }

    /// <summary>Takes the focus from the console, e.g. before a popup opens from the command line.</summary>
    private void TakeFocusFromTerminal() => Focus();

    /// <summary>Console mode: the console has the keys until a panel is chosen again (a click, Esc on an empty line).</summary>
    private void FocusTerminal()
    {
        _consoleMode = true;
        PutFocusInTerminal();
        UpdatePanelKeys();
    }

    /// <summary>
    /// The focus to the console, whichever has the keys (issue #90): a panel "has the keys" as window state while the
    /// console keeps the focus, and the window's key handler routes the keys.
    /// </summary>
    private void PutFocusInTerminal()
    {
        if (_tab is { } tab)
        {
            tab.Terminal.Focus();
            Trace("focus terminal");
        }
    }

    // ----- Shell synchronisation (SPEC §7) -----

    private void SyncShellDirectory()
    {
        // A plugin panel keeps the shell in the folder it was opened from (issue #49).
        if (_shell is { } shell && _active.LocalPath.Length > 0 &&
            !string.Equals(shell.Host.CurrentDirectory, _active.LocalPath, StringComparison.OrdinalIgnoreCase))
        {
            shell.Host.ChangeDirectory(_active.LocalPath);
        }
    }

    /// <summary><c>cd</c> in the shell moves the active panel; the inactive one never follows.</summary>
    private void OnShellDirectoryChanged(string path)
    {
        if (!string.Equals(path, _active.LocalPath, StringComparison.OrdinalIgnoreCase))
        {
            _ = _active.NavigateAsync(path);
        }
    }

    private void OnShellRequestCompleted(string op, string status)
    {
        if (op == "cd" && status != "ok")
        {
            _active.Message = status.StartsWith("error;", StringComparison.Ordinal) ? status[6..] : status;
        }
    }

    private void OnPanelNavigated(FilePanel panel, string path)
    {
        _history.Add(path);
        if (Path.GetPathRoot(path) is { Length: > 0 } root)
        {
            _lastPathPerDrive[root] = path;
        }

        if (panel == _active)
        {
            SyncShellDirectory();
        }
    }

    private void OnPanelChanged(FilePanel panel)
    {
        if (Other(panel).InfoSource == panel)
        {
            Other(panel).RefreshInfo();
        }

        if (Other(panel).QuickViewSource == panel)
        {
            Other(panel).UpdateQuickView();
        }
    }

    /// <summary>
    /// Enter on a file: a program or script runs through PSReadLine, so it lands in history like in Far; any other
    /// file opens in its associated program (issue #52).
    /// </summary>
    private void OnPanelExecuteRequested(FilePanel panel, PanelItem item)
    {
        if (panel != _active)
        {
            Activate(panel);
        }

        if (!Core.FileSystem.RunnableFiles.IsRunnable(item.Name))
        {
            OpenWithAssociation(panel, item.Name);
            return;
        }

        if (_shell is null)
        {
            return;
        }

        SyncShellDirectory();
        _shell.Host.Run(ShellQuote.RunHere(item.Name));
    }

    /// <summary>
    /// Shift+Enter, as in Far (issue #103): a program or script in a new console of its own, which goes on when the app
    /// closes. Any other file opens in its associated program, as Enter does.
    /// </summary>
    private static void RunDetached(FilePanel panel)
    {
        var item = panel.State.Current;
        if (panel.IsPlugin || item.IsParent || item.Entry.IsDirectory || panel.State.Count == 0)
        {
            return;
        }

        if (!Core.FileSystem.RunnableFiles.IsRunnable(item.Name))
        {
            OpenWithAssociation(panel, item.Name);
            return;
        }

        var path = Path.Combine(panel.CurrentPath, item.Name);
        var script = Path.GetExtension(path).Equals(".ps1", StringComparison.OrdinalIgnoreCase);
        try
        {
            // A script would open in its editor through the shell, so PowerShell runs it. A console program started
            // through the shell from this window, which has no console, gets a new one.
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(script ? App.FindPwsh() ?? "pwsh.exe" : path)
            {
                UseShellExecute = true,
                WorkingDirectory = panel.CurrentPath,
                Arguments = script ? $"-File \"{path}\"" : string.Empty,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            panel.Message = ex.Message;
        }
    }

    /// <summary>The file's default shell verb; with no associated program Windows asks which one to use.</summary>
    private static void OpenWithAssociation(FilePanel panel, string name) =>
        OpenWithAssociation(panel, Path.Combine(panel.CurrentPath, name), panel.CurrentPath);

    /// <summary>A file anywhere, a temporary copy from a plugin say; errors go to the panel's message line.</summary>
    private static void OpenWithAssociation(FilePanel panel, string path, string folder)
    {
        var name = Path.GetFileName(path);
        foreach (var verb in new[] { string.Empty, "openas" })
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    Verb = verb,
                    WorkingDirectory = folder,
                });
                return;
            }
            catch (System.ComponentModel.Win32Exception ex) when (verb.Length == 0 && ex.NativeErrorCode == NoAssociation)
            {
                // No program for this type: the next verb shows Windows' "Open with" list.
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode != Cancelled)
                {
                    panel.Message = $"Cannot open '{name}': {ex.Message}";
                }

                return;
            }
        }
    }

    private const int NoAssociation = 1155; // ERROR_NO_ASSOCIATION
    private const int Cancelled = 1223; // ERROR_CANCELLED: the user closed a dialog the shell showed

    /// <summary>
    /// The shell context menu (issue #41). "Rename" goes to the panel's own Shift+F6 when it covers the same items:
    /// the shell's rename verb needs an Explorer view to edit the name in place.
    /// </summary>
    private void OnPanelShellMenuRequested(FilePanel panel, IReadOnlyList<string> names, PixelPoint screen)
    {
        if (panel != _active)
        {
            Activate(panel);
        }

        var selection = panel.State.Selection;
        var sameAsShiftF6 = names.Count > 0 && (selection.Count > 0
            ? names.Count == selection.Count && names.All(selection.Contains)
            : names.Count == 1 && names[0] == panel.State.Current.Name);
        ShellContextMenu.Show(this, panel.State.Path, names, screen, verb =>
        {
            if (sameAsShiftF6 && string.Equals(verb, "rename", StringComparison.OrdinalIgnoreCase))
            {
                Fire(CopyOrMoveAsync(panel, move: true, inPlace: true));
                return true;
            }

            return false;
        });
    }

    /// <summary>The console's cell size, so the layout can snap to whole rows and columns. The zoom scales it with the rest.</summary>
    private void UpdateCellSize() => Workspace.CellSize = TerminalView.MeasureCell(TerminalFontFamily, TerminalEmSize);
}
