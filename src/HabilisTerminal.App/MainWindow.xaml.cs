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
using HabilisTerminal.Shell;
using HabilisTerminal.Shell.ConPty;
using HabilisTerminal.Shell.Integration;

namespace HabilisTerminal.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed when the window closes.")]
public partial class MainWindow : Window
{
    private const string TerminalFontFamily = "Cascadia Mono";
    private const int TerminalFontSize = 11;

    private readonly PlaceholderPanel _left = new("Left");
    private readonly PlaceholderPanel _right = new("Right");
    private PlaceholderPanel _active;
    private (bool Left, bool Right) _restoreVisibility = (true, true);
    private readonly ShellConnection? _shell;
    private GlobalKeyHook? _keyHook;

    public MainWindow()
    {
        InitializeComponent();

        _active = _left;
        _left.IsActive = true;
        WorkspaceLayout.SetRole(_left, WorkspaceRole.LeftPanel);
        WorkspaceLayout.SetRole(_right, WorkspaceRole.RightPanel);
        Workspace.Children.Add(_left);
        Workspace.Children.Add(_right);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _left.DirectoryPath = home;
        _right.DirectoryPath = home;

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

        Loaded += OnLoaded;
        DpiChanged += (_, _) => UpdateCellSize();
        PreviewTextInput += OnPreviewTextInput;
        Closed += (_, _) =>
        {
            _keyHook?.Dispose();
            _shell?.Dispose();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Dark title bar to match the placeholder dark theme; follows the real theme in stage 5.
        var useDarkMode = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
    }

    private bool AnyPanelVisible => Workspace.LeftVisible || Workspace.RightVisible;

    private bool PanelHasFocus => _left.IsKeyboardFocused || _right.IsKeyboardFocused;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _keyHook = new GlobalKeyHook(OnGlobalKey);
        UpdateCellSize();

        Terminal.SetTheme(PlaceholderTheme.Terminal, TerminalFontFamily, TerminalFontSize, PlaceholderTheme.Background);
        Terminal.Connection = _shell;
        FocusActivePanel();
    }

    /// <summary>Keys intercepted before they reach either panels or terminal (SPEC §4, §5).</summary>
    private bool OnGlobalKey(Key key, KeyModifiers modifiers)
    {
        switch (key)
        {
            case Key.O when modifiers == KeyModifiers.Control:
                TogglePanels();
                return true;

            case Key.Tab when modifiers == KeyModifiers.Control && AnyPanelVisible:
                if (PanelHasFocus)
                {
                    FocusTerminal();
                }
                else
                {
                    FocusActivePanel();
                }

                return true;

            case Key.F4 when modifiers == KeyModifiers.Alt:
                Close();
                return true;
        }

        if (!PanelHasFocus)
        {
            return false;
        }

        switch (key)
        {
            case Key.F1 when modifiers == KeyModifiers.Control:
                SetPanelVisibility(!Workspace.LeftVisible, Workspace.RightVisible);
                return true;

            case Key.F2 when modifiers == KeyModifiers.Control:
                SetPanelVisibility(Workspace.LeftVisible, !Workspace.RightVisible);
                return true;

            case Key.Tab when modifiers == KeyModifiers.None:
                var other = _active == _left ? _right : _left;
                if (IsShown(other))
                {
                    Activate(other);
                    SyncShellDirectory();
                }

                return true;

            case Key.PageUp when modifiers == KeyModifiers.Control:
                Navigate(_active, System.IO.Path.GetDirectoryName(_active.DirectoryPath.TrimEnd('\\')) ?? _active.DirectoryPath);
                return true;

            case Key.Oem5 when modifiers == KeyModifiers.Control: // Ctrl+\
                Navigate(_active, System.IO.Path.GetPathRoot(_active.DirectoryPath) ?? _active.DirectoryPath);
                return true;

            // Command line keys that work from panel focus, as in Far.
            case Key.E when modifiers == KeyModifiers.Control:
                _shell?.Host.SendKey(Win32InputSequence.VkUp, controlKeyState: Win32InputSequence.EnhancedKey);
                return true;

            case Key.X when modifiers == KeyModifiers.Control:
                _shell?.Host.SendKey(Win32InputSequence.VkDown, controlKeyState: Win32InputSequence.EnhancedKey);
                return true;

            case Key.Space when modifiers == KeyModifiers.Control:
                _shell?.Host.SendKey(Win32InputSequence.VkSpace, ' ', Win32InputSequence.LeftCtrl);
                return true;
        }

        return false;
    }

    /// <summary>Navigation in a panel; the active panel takes the shell with it (SPEC §7).</summary>
    private void Navigate(PlaceholderPanel panel, string path)
    {
        panel.DirectoryPath = path;
        panel.Message = null;
        if (panel == _active)
        {
            SyncShellDirectory();
        }
    }

    private void SyncShellDirectory()
    {
        if (_shell is { } shell &&
            !string.Equals(shell.Host.CurrentDirectory, _active.DirectoryPath, StringComparison.OrdinalIgnoreCase))
        {
            shell.Host.ChangeDirectory(_active.DirectoryPath);
        }
    }

    /// <summary><c>cd</c> in the shell moves the active panel; the inactive one never follows.</summary>
    private void OnShellDirectoryChanged(string path)
    {
        _active.DirectoryPath = path;
        _active.Message = null;
    }

    private void OnShellRequestCompleted(string op, string status)
    {
        if (op == "cd" && status != "ok")
        {
            _active.Message = status.StartsWith("error;", StringComparison.Ordinal) ? status[6..] : status;
        }
    }

    /// <summary>Printable characters typed in a panel go to the command line (SPEC §4).</summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!PanelHasFocus || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl))
        {
            return;
        }

        e.Handled = true;
        FocusTerminal();
        _shell?.WriteInput(e.Text);
    }

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
            _active = left ? _left : _right;
        }

        if (!wasVisible || PanelHasFocus || !IsShown(FocusedPanel()))
        {
            Activate(_active);
        }
    }

    private PlaceholderPanel? FocusedPanel() =>
        _left.IsKeyboardFocused ? _left : _right.IsKeyboardFocused ? _right : null;

    private bool IsShown(PlaceholderPanel? panel) =>
        panel == _left ? Workspace.LeftVisible : panel == _right && Workspace.RightVisible;

    private void Activate(PlaceholderPanel panel)
    {
        _active = panel;
        _left.IsActive = panel == _left;
        _right.IsActive = panel == _right;
        FocusActivePanel();
    }

    private void FocusActivePanel()
    {
        // The panel may only become visible after the next layout pass.
        Dispatcher.BeginInvoke(() => _active.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void FocusTerminal()
    {
        var host = FindHwndHost(Terminal);
        if (host is not null && host.Handle != IntPtr.Zero)
        {
            Keyboard.ClearFocus();
            SetFocus(host.Handle);
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
