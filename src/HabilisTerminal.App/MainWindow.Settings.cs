using System.IO;
using HabilisTerminal.App.Reporting;
using HabilisTerminal.Core.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Viewing;
using HabilisTerminal.Core.Panels;
using HabilisTerminal.Core.Settings;

namespace HabilisTerminal.App;

/// <summary>Settings applied live and state restored on start / saved on exit (SPEC §14).</summary>
public partial class MainWindow
{
    private const int FileHistoryCapacity = 50;
    private const string SettingsNoticeKey = "settings";
    private const string ThemeNoticeKey = "theme";

    private readonly List<FileHistoryEntry> _fileHistory = [];
    private AppSettings _settings = App.Settings?.Current ?? AppSettings.Defaults;
    private AppSettings? _appliedSettings;
    private System.Windows.Threading.DispatcherTimer? _stateTimer;

    private string TerminalFontFamily => _settings.TerminalFontFamily;

    private int TerminalFontSize => Math.Clamp(_settings.TerminalFontSize + _terminalZoom, 6, 48);

    /// <summary>Applies settings that can change while running; the shell settings apply on the next start.</summary>
    private void ApplySettings(AppSettings settings)
    {
        using var measure = Perf.Measure("settings.apply");
        var previous = _appliedSettings;
        _settings = settings;
        _appliedSettings = settings;
        if (App.Paths is { } paths)
        {
            PerfDiagnostics.Apply(settings.DiagnosticsPerformance, Dispatcher, paths.DiagnosticsDirectory);
        }

        _themes.ThemesDirectory = App.Paths?.ThemesDirectory;
        _themes.Configure(settings.Theme, settings.UseAccentColor, force: previous is null);
        ShowThemeError();

        var repaintPanels = IconProvider.SetTheme(settings.IconTheme);
        if (previous is null || previous.PanelsFontFamily != settings.PanelsFontFamily || previous.PanelsFontSize != settings.PanelsFontSize)
        {
            FileListView.SetFont(settings.PanelsFontFamily, settings.PanelsFontSize);
            repaintPanels = true;
        }

        if (previous is not null && previous.PanelsShowHidden != settings.PanelsShowHidden)
        {
            foreach (var panel in AllPanels())
            {
                panel.State.SetShowHidden(settings.PanelsShowHidden);
            }

            repaintPanels = true;
        }

        FilePanel.AutoFolderSizes = settings.PanelsFolderSizes;
        if (previous is not null && previous.PanelsFolderSizes != settings.PanelsFolderSizes)
        {
            foreach (var panel in AllPanels())
            {
                panel.RequestFolderSizes();
            }
        }

        QuickView.Settings = settings;
        QuickView.Theme = _themes.Current;
        if (previous is null || previous.ViewerFontFamily != settings.ViewerFontFamily || previous.ViewerFontSize != settings.ViewerFontSize)
        {
            ViewerView.SetFont(settings.ViewerFontFamily, settings.ViewerFontSize);
        }

        if (repaintPanels && previous is not null)
        {
            foreach (var panel in AllPanels())
            {
                panel.InvalidatePanel();
            }
        }

        if (previous is not null && IsLoaded && (previous.TerminalFontFamily != settings.TerminalFontFamily || previous.TerminalFontSize != settings.TerminalFontSize))
        {
            ApplyTerminalTheme(_themes.Current);
            UpdateCellSize();
        }

        if (PageHost.Content is SettingsUi.SettingsPage page)
        {
            page.Refresh();
        }

        ShowSettingsErrors();
    }

    private void OnSettingsFileChanged(AppSettings settings) => Dispatcher.BeginInvoke(() => ApplySettings(settings));

    private void ShowSettingsErrors()
    {
        if (App.Settings is not { } store || !IsLoaded)
        {
            return;
        }

        if (store.Errors.Count == 0)
        {
            Toasts.Dismiss(SettingsNoticeKey);
            return;
        }

        var lines = store.Errors.Take(3).Select(e => e.ToString());
        var more = store.Errors.Count > 3 ? $"\n…and {store.Errors.Count - 3} more." : string.Empty;
        Toasts.Notify(SettingsNoticeKey, "Settings were not fully applied", string.Join("\n", lines) + more + "\nThe last valid values stay in effect.",
            ("Open settings.json", OpenSettingsFile));
    }

    private void ShowThemeError()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (_themes.LastError is { } error)
        {
            Toasts.Notify(ThemeNoticeKey, "Theme not applied", error + "\nThe built-in theme is used instead.", ("Open settings.json", OpenSettingsFile));
        }
        else
        {
            Toasts.Dismiss(ThemeNoticeKey);
        }
    }

    /// <summary>Opens settings.json in the built-in editor (created with a schema reference when missing).</summary>
    private void OpenSettingsFile()
    {
        if (App.Settings is not { } store)
        {
            return;
        }

        try
        {
            if (!File.Exists(store.FilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
                File.WriteAllText(store.FilePath, "{\n    \"$schema\": \"./settings.schema.json\"\n}\n");
            }

            Edit(store.FilePath, _active, followFile: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Widgets.OperationDialogs.Message(this, "Settings", ex.Message);
        }
    }

    // ----- State -----

    /// <summary>
    /// Window placement in physical pixels of the monitor it was on (SPEC §13). Applied once the window handle exists and
    /// before it is shown: <c>SetWindowPlacement</c> keeps the window on its monitor (re-scaled when that monitor's DPI
    /// differs), maximises it there, and moves it into view when that monitor is gone.
    /// </summary>
    private void RestoreWindowPlacement(AppState state)
    {
        if (state.Window is not { } placement || placement.Width < 200 || placement.Height < 150)
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) =>
        {
            var native = new WINDOWPLACEMENT
            {
                length = Marshal.SizeOf<WINDOWPLACEMENT>(),
                showCmd = placement.Maximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL,
                rcNormalPosition = new RECT
                {
                    Left = (int)placement.Left,
                    Top = (int)placement.Top,
                    Right = (int)(placement.Left + placement.Width),
                    Bottom = (int)(placement.Top + placement.Height),
                },
            };
            SetWindowPlacement(new WindowInteropHelper(this).Handle, ref native);
        };
    }

    private WindowPlacement? CaptureWindowPlacement()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var native = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (handle == IntPtr.Zero || !GetWindowPlacement(handle, ref native))
        {
            return null;
        }

        var bounds = native.rcNormalPosition;
        return new WindowPlacement(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, WindowPlacement.IsMaximized(native.showCmd, native.flags));
    }

    private void RestoreLayout(AppState state)
    {
        Workspace.TerminalRows = state.TerminalRows ?? _settings.TerminalDefaultRows;
        if (state.SplitRatio is { } split)
        {
            Workspace.SplitRatio = split;
        }

        if (state.SinglePanelRatio is { } single)
        {
            Workspace.SinglePanelRatio = single;
        }

        foreach (var path in state.FolderHistory.Reverse())
        {
            _history.Add(path);
        }

        _fileHistory.AddRange(state.FileHistory.Take(FileHistoryCapacity));
        _pluginHotkeys = new Core.Plugins.PluginHotkeys(state.PluginHotkeys);
        RestorePanel(_left, state.Left);
        RestorePanel(_right, state.Right);
        if (!state.Left.Visible || !state.Right.Visible)
        {
            Workspace.LeftVisible = state.Left.Visible;
            Workspace.RightVisible = state.Right.Visible;
            UpdatePanelsShown();
        }

        // A shown panel is always active: a saved active panel that is hidden gives way to the visible one.
        var visible = (state.Left.Visible, state.Right.Visible);
        if (!PanelVisibilityRule.ActiveIsLeft(state.ActivePanel != _right.Side, visible, visible))
        {
            Activate(_right);
        }
    }

    private void RestorePanel(FilePanel panel, PanelSnapshot snapshot)
    {
        panel.State.ViewMode = snapshot.ViewMode;
        panel.State.SetSort(panel.State.Sort with { Mode = snapshot.Sort, Descending = snapshot.SortDescending });
        panel.State.SetSelectedFirst(snapshot.SelectedFirst);
        panel.State.SetShowHidden(snapshot.ShowHidden ?? _settings.PanelsShowHidden);
    }

    /// <summary>The restored panel path, or the user profile when it no longer exists.</summary>
    private static string StartPath(PanelSnapshot snapshot, string home) =>
        snapshot.Path is { Length: > 0 } path && Directory.Exists(path) ? path : home;

    private AppState CaptureState()
    {
        return new AppState
        {
            Left = Snapshot(_left, Workspace.LeftVisible),
            Right = Snapshot(_right, Workspace.RightVisible),
            ActivePanel = _active.Side,
            TerminalRows = Workspace.TerminalRows,
            SplitRatio = Workspace.SplitRatio,
            SinglePanelRatio = Workspace.SinglePanelRatio,
            Window = CaptureWindowPlacement(),
            FolderHistory = _history.RecentFirst,
            FileHistory = [.. _fileHistory],
            PluginHotkeys = _pluginHotkeys.Snapshot(),
        };
    }

    private static PanelSnapshot Snapshot(FilePanel panel, bool visible) => new()
    {
        Path = panel.LocalPath, // a plugin panel reopens on its local folder
        ViewMode = panel.State.ViewMode,
        Sort = panel.State.Sort.Mode,
        SortDescending = panel.State.Sort.Descending,
        SelectedFirst = panel.State.SelectedFirst,
        ShowHidden = panel.State.ShowHidden,
        Visible = visible,
        CurrentName = panel.IsPlugin || panel.State.Count == 0 || panel.State.Current.IsParent ? null : panel.State.Current.Name,
    };

    private void SaveState()
    {
        using var measure = Perf.Measure("state.save");
        try
        {
            App.State?.Save(CaptureState());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace($"state not saved: {ex.Message}");
        }
    }

    /// <summary>State is also written every minute, so a crash loses little (SPEC §14).</summary>
    private void StartStateTimer()
    {
        _stateTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMinutes(1), System.Windows.Threading.DispatcherPriority.Background, (_, _) => SaveState(), Dispatcher);
        _stateTimer.Start();
    }

    /// <summary>Alt+F11 history: most recent first, one entry per file.</summary>
    private void RememberFile(string path, bool edited)
    {
        _fileHistory.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        _fileHistory.Insert(0, new FileHistoryEntry(path, edited));
        if (_fileHistory.Count > FileHistoryCapacity)
        {
            _fileHistory.RemoveAt(_fileHistory.Count - 1);
        }
    }

    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMAXIMIZED = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);
}
