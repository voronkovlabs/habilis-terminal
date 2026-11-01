using System.IO;
using System.Windows;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Viewing;
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

    private int TerminalFontSize => _settings.TerminalFontSize;

    /// <summary>Applies settings that can change while running; the shell settings apply on the next start.</summary>
    private void ApplySettings(AppSettings settings)
    {
        var previous = _appliedSettings;
        _settings = settings;
        _appliedSettings = settings;

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
            _left.State.SetShowHidden(settings.PanelsShowHidden);
            _right.State.SetShowHidden(settings.PanelsShowHidden);
            repaintPanels = true;
        }

        if (previous is null || previous.ViewerFontFamily != settings.ViewerFontFamily || previous.ViewerFontSize != settings.ViewerFontSize)
        {
            ViewerView.SetFont(settings.ViewerFontFamily, settings.ViewerFontSize);
        }

        if (repaintPanels && previous is not null)
        {
            _left.InvalidatePanel();
            _right.InvalidatePanel();
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

    /// <summary>Window size and position before the window is shown; ignored when it would be off every screen.</summary>
    private void RestoreWindowPlacement(AppState state)
    {
        if (state.Window is not { } placement || placement.Width < 200 || placement.Height < 150)
        {
            return;
        }

        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        var visible = Rect.Intersect(screen, bounds);
        if (visible.IsEmpty || visible.Width < 100 || visible.Height < 50)
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.Maximized)
        {
            SourceInitialized += (_, _) => WindowState = WindowState.Maximized;
        }
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
        RestorePanel(_left, state.Left);
        RestorePanel(_right, state.Right);
        if (!state.Left.Visible || !state.Right.Visible)
        {
            Workspace.LeftVisible = state.Left.Visible;
            Workspace.RightVisible = state.Right.Visible;
        }

        if (state.ActivePanel == _right.Side && state.Right.Visible)
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
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return new AppState
        {
            Left = Snapshot(_left, Workspace.LeftVisible),
            Right = Snapshot(_right, Workspace.RightVisible),
            ActivePanel = _active.Side,
            TerminalRows = Workspace.TerminalRows,
            SplitRatio = Workspace.SplitRatio,
            SinglePanelRatio = Workspace.SinglePanelRatio,
            Window = bounds.IsEmpty ? null : new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized),
            FolderHistory = _history.RecentFirst,
            FileHistory = [.. _fileHistory],
        };
    }

    private static PanelSnapshot Snapshot(FilePanel panel, bool visible) => new()
    {
        Path = panel.CurrentPath,
        ViewMode = panel.State.ViewMode,
        Sort = panel.State.Sort.Mode,
        SortDescending = panel.State.Sort.Descending,
        SelectedFirst = panel.State.SelectedFirst,
        ShowHidden = panel.State.ShowHidden,
        Visible = visible,
    };

    private void SaveState()
    {
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
}
