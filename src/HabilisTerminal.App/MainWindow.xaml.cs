using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using HabilisTerminal.App.Hosting;
using HabilisTerminal.App.Input;
using HabilisTerminal.App.Layout;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Theming;
using HabilisTerminal.Core.Panels;
using HabilisTerminal.Shell;
using HabilisTerminal.Shell.Integration;

namespace HabilisTerminal.App;

/// <summary>
/// The window: two file panels and the terminal (SPEC §3), focus and visibility (SPEC §4), shell synchronisation
/// (SPEC §7). Panel commands live in MainWindow.Commands.cs.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed when the window closes.")]
public partial class MainWindow : Window
{
    private readonly ThemeService _themes = new();
    private readonly FolderHistory _history = new();
    private Core.Plugins.PluginHotkeys _pluginHotkeys = new();
    private readonly Dictionary<string, string> _lastPathPerDrive = new(StringComparer.OrdinalIgnoreCase);
    private readonly ShellConnection? _shell;
    private FilePanel _left = new("Left");
    private FilePanel _right = new("Right");
    private FilePanel _active;
    private (bool Left, bool Right) _restoreVisibility = (true, true);
    private readonly HiddenPanelReveal _reveal = new();
    private bool _revealFromTerminal;
    private GlobalKeyHook? _keyHook;
    private System.Windows.Threading.DispatcherTimer? _themeReloadTimer;
    private readonly Widgets.TitleBar _titleBar;

    public MainWindow()
    {
        InitializeComponent();
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
        DockPanel.SetDock(_titleBar, Dock.Top);
        Root.Children.Insert(0, _titleBar);

        // Maximized, a WindowChrome window reaches past the screen by its resize frame; keep the content on screen.
        void ApplyInset() => Root.Margin = WindowState == WindowState.Maximized ? Widgets.TitleBar.MaximizedInset(this) : default;
        StateChanged += (_, _) => ApplyInset();
        Loaded += (_, _) => ApplyInset(); // a restored maximized placement raises no StateChanged

        var state = App.State?.Load() ?? new Core.Settings.AppState();
        RestoreWindowPlacement(state);
        _themes.Changed += OnThemeChanged;
        ApplySettings(_settings);

        _active = _left;
        _left.IsActive = true;
        WorkspaceLayout.SetRole(_left, WorkspaceRole.LeftPanel);
        WorkspaceLayout.SetRole(_right, WorkspaceRole.RightPanel);
        Workspace.Children.Add(_left);
        Workspace.Children.Add(_right);
        foreach (var panel in new[] { _left, _right })
        {
            panel.Navigated += OnPanelNavigated;
            panel.ExecuteRequested += OnPanelExecuteRequested;
            panel.ArchiveRequested += (p, item, anyFormat) => Fire(OpenArchiveAsync(p, item, anyFormat));
            panel.ShellMenuRequested += OnPanelShellMenuRequested;
            panel.Changed += OnPanelChanged;
            panel.GotKeyboardFocus += (_, _) => OnPanelFocused(panel);
            panel.GotKeyboardFocus += (_, _) =>
            {
                CollapseConsole(stopGrowing: true); // issue #82
                OnPanelGotFocus(panel); // issue #90: the panel gets the keys, the console keeps the Win32 focus
            };
        }

        LoadPlugins();
        RestoreLayout(state);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // App checks for pwsh before creating the window; this only guards against it disappearing in between.
        var pwsh = App.FindPwsh() ?? throw new InvalidOperationException("PowerShell 7 (pwsh.exe) was not found.");

        var leftStart = StartPath(state.Left, home);
        var rightStart = StartPath(state.Right, home);
        var host = new ShellHost(new ShellHostOptions(
            pwsh,
            ShellScripts.IntegrationScriptPath,
            _active == _right ? rightStart : leftStart,
            NullIfEmpty(_settings.ShellInitCommand),
            ExtraArguments: NullIfEmpty(_settings.ShellArgs),
            Integration: _settings.ShellIntegration));
        host.Exited += _ => Dispatcher.BeginInvoke(Close);
        host.DirectoryChanged += path => Dispatcher.BeginInvoke(() => OnShellDirectoryChanged(path));
        host.FocusPanelRequested += () => Dispatcher.BeginInvoke(() =>
        {
            if (!IsPageOpen && ConsoleHasKeys && IsShown(_active))
            {
                FocusActivePanel();
            }
        });
        host.RequestCompleted += (_, op, status) => Dispatcher.BeginInvoke(() => OnShellRequestCompleted(op, status));
        host.AddressEntered += address => Dispatcher.BeginInvoke(() => OnPluginAddress(address));
        host.AddressPrefixes = _plugins.Prefixes();
        WireOverflow(host);
        WireCommandLine(host);
        _shell = new ShellConnection(host);
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

            StartStateTimer();
            App.Updates.UpdateReady += OnUpdateReady;
            await _left.NavigateAsync(leftStart);
            await _right.NavigateAsync(rightStart);
        };
        DpiChanged += (_, _) => UpdateCellSize();
        PreviewTextInput += OnPreviewTextInput;
        PreviewKeyDown += OnPreviewArrowKey;
        Closing += (_, e) =>
        {
            // Unsaved edits are never lost silently, whatever closes the window (F10, Alt+F4, the close button).
            if (PageHost.Content is ILeavePage page && !page.ConfirmLeave())
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

            _keyHook?.Dispose();
            _shell?.Dispose();
            _themes.Dispose();
        };
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private bool AnyPanelVisible => Workspace.LeftVisible || Workspace.RightVisible;

    private bool PanelHasFocus => FocusedPanel() is not null;

    private FilePanel Other(FilePanel panel) => panel == _left ? _right : _left;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UpdateTitleBar();

        // Windows mode, high contrast and accent changes repaint everything live (SPEC §10).
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
        var themeRelated = message == WM_DWMCOLORIZATIONCOLORCHANGED ||
            (message == WM_SETTINGCHANGE && (lParam == IntPtr.Zero || Marshal.PtrToStringUni(lParam) is "ImmersiveColorSet" or "WindowsThemeElement"));
        if (themeRelated)
        {
            _themeReloadTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(300), System.Windows.Threading.DispatcherPriority.Background, (_, _) =>
            {
                _themeReloadTimer!.Stop();
                _themes.Reload();
            }, Dispatcher);
            _themeReloadTimer.Stop();
            _themeReloadTimer.Start();
        }

        return IntPtr.Zero;
    }

    private void OnThemeChanged(Themes.ColorTheme theme)
    {
        Background = PanelPalette.Background;
        QuickView.Theme = theme;
        IconProvider.OnColorThemeChanged();
        _left.ApplyTheme();
        _right.ApplyTheme();
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
        }
    }

    private void ApplyTerminalTheme(Themes.ColorTheme theme)
    {
        var background = theme.GetOpaque("terminal.background");
        Terminal.SetTheme(ThemeService.ToTerminalTheme(theme), TerminalFontFamily, (short)TerminalFontSize, Color.FromRgb(background.R, background.G, background.B));
    }

    private void UpdateTitleBar()
    {
        var useDarkMode = _themes.Current.IsLight ? 0 : 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
    }

    private void OnLoaded()
    {
        _keyHook = new GlobalKeyHook(OnGlobalKey, OnTerminalMouse, window => TerminalHandle is var terminal && terminal != IntPtr.Zero && IsChildOrSelf(terminal, window));
        UpdateCellSize();

        ApplyTerminalTheme(_themes.Current);
        Terminal.Connection = _shell;

        // The control sizes the connection only when its own size changes later, so the shell started at the
        // connection's default 120x30 and kept it until the layout changed (found in issue #82). Tell it the real size.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (Terminal.Rows > 0 && Terminal.Columns > 0)
            {
                _shell?.Resize((uint)Terminal.Rows, (uint)Terminal.Columns);
            }
        });
        FocusActivePanel();
    }

    /// <summary>
    /// Arrow keys in a panel. WPF dispatches a key before the global hook sees it, and an arrow nobody handled moves
    /// focus to the neighbouring element (Left in the right panel jumped to the left panel), so arrows are taken here.
    /// </summary>
    private void OnPreviewArrowKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // With the Win32 focus in the console the window's key hook routes every key (issue #90).
        if (key is not (Key.Left or Key.Right or Key.Up or Key.Down) || TerminalHasFocus || FocusedPanel() is null)
        {
            return;
        }

        var modifiers = KeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= KeyModifiers.Alt;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            modifiers |= KeyModifiers.Control;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= KeyModifiers.Shift;
        }

        OnGlobalKey(key, modifiers);
        e.Handled = true;
    }

    /// <summary>Printable characters typed in a panel go to the command line, or to quick search while it is open (SPEC §4).</summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (TerminalHasFocus || FocusedPanel() is not { } panel || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl))
        {
            return;
        }

        e.Handled = true;
        if (panel.IsQuickSearchActive)
        {
            panel.AppendQuickSearch(e.Text);
            return;
        }

        // As in Far, the focus stays on the panel (issue #85).
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
    /// The panel that has the keys (issue #90): the active panel while the console holds the Win32 focus, outside
    /// console mode and while no command runs. Otherwise a panel that holds the WPF focus for a moment (a click,
    /// before the focus goes back to the console).
    /// </summary>
    private FilePanel? FocusedPanel()
    {
        if (TerminalHasFocus)
        {
            return !_consoleMode && !_commandRunning && IsShown(_active) ? _active : null;
        }

        return _left.IsKeyboardFocused ? _left : _right.IsKeyboardFocused ? _right : null;
    }

    private static bool IsChildOrSelf(IntPtr parent, IntPtr window)
    {
        for (var current = window; current != IntPtr.Zero; current = GetParent(current))
        {
            if (current == parent)
            {
                return true;
            }
        }

        return false;
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

    /// <summary>The active panel gets the keys (issue #90); the Win32 focus goes to the console, as always.</summary>
    private void FocusActivePanel()
    {
        _consoleMode = false;

        // The panel may only become visible after the next layout pass.
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsPageOpen)
            {
                PutFocusInTerminal();
            }

            UpdatePanelKeys();
            Trace($"keys to panel {_active.Side}, win32={GetFocus():X}");
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// Moves Win32 focus from the terminal back to the window, e.g. before a popup opens from the command line.
    /// A plain SetFocus is not enough: WPF hands focus straight back to the child window it remembers, so the window
    /// itself takes the WPF focus (a panel would hand it straight back to the console, issue #90).
    /// </summary>
    private void TakeFocusFromTerminal()
    {
        SetFocus(new WindowInteropHelper(this).Handle);
        FocusManager.SetFocusedElement(this, this);
        Keyboard.Focus(this);
    }

    /// <summary>Console mode: the console has the keys until a panel is chosen again (a click, Esc on an empty line).</summary>
    private void FocusTerminal()
    {
        _consoleMode = true;
        PutFocusInTerminal();
        UpdatePanelKeys();
    }

    /// <summary>
    /// The Win32 focus to the console, whichever has the keys. WPF keeps no focused element (issue #90): a remembered
    /// panel would get the focus back, and with it the keys, when the window is activated again. The console's keys
    /// bypass WPF (<see cref="GlobalKeyHook"/>).
    /// </summary>
    private void PutFocusInTerminal()
    {
        var host = FindHwndHost(Terminal);
        if (host is not null && host.Handle != IntPtr.Zero)
        {
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, null);
            SetFocus(host.Handle);
            Trace($"focus terminal {host.Handle:X}");
        }
    }

    private static HwndHost? FindHwndHost(DependencyObject root)
    {
        if (root is HwndHost host)
        {
            return host;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindHwndHost(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
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
        _shell.Host.Run("& " + ShellQuote.Quote(".\\" + item.Name));
    }

    /// <summary>The file's default shell verb; with no associated program Windows asks which one to use.</summary>
    private static void OpenWithAssociation(FilePanel panel, string name)
    {
        var path = Path.Combine(panel.CurrentPath, name);
        foreach (var verb in new[] { string.Empty, "openas" })
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    Verb = verb,
                    WorkingDirectory = panel.CurrentPath,
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
    private void OnPanelShellMenuRequested(FilePanel panel, IReadOnlyList<string> names, Point screen)
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

    /// <summary>Estimates the terminal cell size so the layout can snap to whole rows and columns.</summary>
    private void UpdateCellSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var emSize = TerminalFontSize * 96.0 / 72.0;
        var typeface = new Typeface(TerminalFontFamily);
        var text = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, emSize, Brushes.White, dpi.PixelsPerDip);
        var lineHeight = new FontFamily(TerminalFontFamily).LineSpacing * emSize;

        Workspace.CellSize = new Size(
            Math.Round(text.WidthIncludingTrailingWhitespace * dpi.DpiScaleX) / dpi.DpiScaleX,
            Math.Round(lineHeight * dpi.DpiScaleY) / dpi.DpiScaleY);
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
