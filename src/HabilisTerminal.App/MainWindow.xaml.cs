using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
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
    private const string TerminalFontFamily = "Cascadia Mono";
    private const int TerminalFontSize = 11;

    private readonly ThemeService _themes = new();
    private readonly FolderHistory _history = new();
    private readonly Dictionary<string, string> _lastPathPerDrive = new(StringComparer.OrdinalIgnoreCase);
    private readonly ShellConnection? _shell;
    private FilePanel _left = new("Left");
    private FilePanel _right = new("Right");
    private FilePanel _active;
    private (bool Left, bool Right) _restoreVisibility = (true, true);
    private GlobalKeyHook? _keyHook;
    private System.Windows.Threading.DispatcherTimer? _themeReloadTimer;

    public MainWindow()
    {
        InitializeComponent();

        _themes.Changed += OnThemeChanged;
        _themes.Reload();

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
            panel.Changed += OnPanelChanged;
            panel.GotKeyboardFocus += (_, _) => OnPanelFocused(panel);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pwsh = PwshLocator.Find();
        if (pwsh is null)
        {
            MessageBox.Show(
                "PowerShell 7 (pwsh.exe) was not found.\n\nInstall it with:\n    winget install Microsoft.PowerShell",
                "Habilis Terminal", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        // Development hook: PowerShell code run before the integration script in test sessions, e.g. loading
        // a prompt theme without touching the user's profile. Replaced by settings in stage 10.
        var initCommand = Environment.GetEnvironmentVariable("HABI_SHELL_INIT");
        var host = new ShellHost(new ShellHostOptions(pwsh, ShellScripts.IntegrationScriptPath, home, initCommand));
        host.Exited += _ => Dispatcher.BeginInvoke(Close);
        host.DirectoryChanged += path => Dispatcher.BeginInvoke(() => OnShellDirectoryChanged(path));
        host.RequestCompleted += (_, op, status) => Dispatcher.BeginInvoke(() => OnShellRequestCompleted(op, status));
        _shell = new ShellConnection(host);

        Loaded += async (_, _) =>
        {
            OnLoaded();
            await _left.NavigateAsync(home);
            await _right.NavigateAsync(home);
        };
        DpiChanged += (_, _) => UpdateCellSize();
        PreviewTextInput += OnPreviewTextInput;
        Closed += (_, _) =>
        {
            _keyHook?.Dispose();
            _shell?.Dispose();
            _themes.Dispose();
        };
    }

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
        IconProvider.OnColorThemeChanged();
        _left.ApplyTheme();
        _right.ApplyTheme();
        if (IsLoaded)
        {
            ApplyTerminalTheme(theme);
            UpdateTitleBar();
        }
    }

    private void ApplyTerminalTheme(Themes.ColorTheme theme)
    {
        var background = theme.GetOpaque("terminal.background");
        Terminal.SetTheme(ThemeService.ToTerminalTheme(theme), TerminalFontFamily, TerminalFontSize, Color.FromRgb(background.R, background.G, background.B));
    }

    private void UpdateTitleBar()
    {
        var useDarkMode = _themes.Current.IsLight ? 0 : 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
    }

    private void OnLoaded()
    {
        _keyHook = new GlobalKeyHook(OnGlobalKey);
        UpdateCellSize();

        ApplyTerminalTheme(_themes.Current);
        Terminal.Connection = _shell;
        FocusActivePanel();
    }

    /// <summary>Printable characters typed in a panel go to the command line, or to quick search while it is open (SPEC §4).</summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (FocusedPanel() is not { } panel || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl))
        {
            return;
        }

        e.Handled = true;
        if (panel.IsQuickSearchActive)
        {
            panel.AppendQuickSearch(e.Text);
            return;
        }

        FocusTerminal();
        _shell?.WriteInput(e.Text);
    }

    // ----- Visibility and focus -----

    private void TogglePanels()
    {
        if (AnyPanelVisible)
        {
            _restoreVisibility = (Workspace.LeftVisible, Workspace.RightVisible);
            SetPanelVisibility(false, false);
        }
        else
        {
            SetPanelVisibility(_restoreVisibility.Left, _restoreVisibility.Right);
        }
    }

    private void SetPanelVisibility(bool left, bool right)
    {
        var wasVisible = AnyPanelVisible;
        if (wasVisible && !left && !right)
        {
            _restoreVisibility = (Workspace.LeftVisible, Workspace.RightVisible);
        }

        Workspace.LeftVisible = left;
        Workspace.RightVisible = right;

        if (!AnyPanelVisible)
        {
            FocusTerminal();
            return;
        }

        if (!IsShown(_active))
        {
            Activate(left ? _left : _right);
            SyncShellDirectory();
        }

        if (!wasVisible || PanelHasFocus || !IsShown(FocusedPanel()))
        {
            FocusActivePanel();
        }
    }

    /// <summary>
    /// The panel with keyboard focus. WPF can keep a panel as its focused element while Win32 focus is in the terminal
    /// window, so the terminal's real focus wins.
    /// </summary>
    private FilePanel? FocusedPanel()
    {
        if (FindHwndHost(Terminal) is { Handle: var terminal } && terminal != IntPtr.Zero && IsChildOrSelf(terminal, GetFocus()))
        {
            return null;
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
    }

    private void OnPanelFocused(FilePanel panel)
    {
        if (panel != _active)
        {
            Activate(panel);
            SyncShellDirectory();
        }
    }

    private void FocusActivePanel()
    {
        // The panel may only become visible after the next layout pass. Win32 focus is taken back from the terminal
        // window first: WPF may still consider the panel focused and would not move it otherwise.
        Dispatcher.BeginInvoke(() =>
        {
            SetFocus(new WindowInteropHelper(this).Handle);
            Keyboard.ClearFocus();
            var focused = _active.Focus();
            Trace($"focus panel {_active.Side}: {focused}, win32={GetFocus():X}");
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void FocusTerminal()
    {
        var host = FindHwndHost(Terminal);
        if (host is not null && host.Handle != IntPtr.Zero)
        {
            Keyboard.ClearFocus();
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
        if (_shell is { } shell && _active.CurrentPath.Length > 0 &&
            !string.Equals(shell.Host.CurrentDirectory, _active.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            shell.Host.ChangeDirectory(_active.CurrentPath);
        }
    }

    /// <summary><c>cd</c> in the shell moves the active panel; the inactive one never follows.</summary>
    private void OnShellDirectoryChanged(string path)
    {
        if (!string.Equals(path, _active.CurrentPath, StringComparison.OrdinalIgnoreCase))
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
    }

    /// <summary>Enter on a file: run it through PSReadLine, so it lands in history like in Far.</summary>
    private void OnPanelExecuteRequested(FilePanel panel, PanelItem item)
    {
        if (_shell is null)
        {
            return;
        }

        if (panel != _active)
        {
            Activate(panel);
        }

        SyncShellDirectory();
        _shell.Host.Run("& " + ShellQuote.Quote(".\\" + item.Name));
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
