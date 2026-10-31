using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HabilisTerminal.App.Input;
using HabilisTerminal.App.Theming;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Panels;

namespace HabilisTerminal.App.Panels;

/// <summary>
/// One file panel: header with the path, the file list (or the info view), status line and quick search.
/// Loads directories in the background (first batch at once, the rest after), follows changes through
/// <see cref="DirectoryWatcher"/>, and handles the panel's own keys (SPEC §5).
/// </summary>
public sealed class FilePanel : Border
{
    private readonly FileListView _list = new();
    private readonly InfoPanelView _info = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _path = new();
    private readonly TextBlock _message = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _statusRight = new();
    private readonly Border _quickSearch = new();
    private readonly TextBlock _quickSearchText = new();
    private readonly Grid _body = new();
    private CancellationTokenSource? _loading;
    private DirectoryWatcher? _watcher;
    private bool _isActive;

    public FilePanel(string side)
    {
        Side = side;
        Focusable = true;
        FocusVisualStyle = null;
        Background = PanelPalette.Background;
        BorderThickness = new Thickness(1);
        BorderBrush = PanelPalette.Border;

        _list.State = State;
        _list.ItemClicked += OnItemClicked;
        _list.HeaderClicked += mode =>
        {
            State.ApplySortKey(mode);
            Refresh();
        };
        _info.Visibility = Visibility.Collapsed;

        _title.FontFamily = _path.FontFamily = _status.FontFamily = _statusRight.FontFamily = _message.FontFamily = new FontFamily("Segoe UI");
        _title.FontSize = 11;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = PanelPalette.Foreground;
        _title.Margin = new Thickness(0, 0, 8, 0);
        _path.FontSize = 12;
        _path.Foreground = PanelPalette.DescriptionForeground;
        _path.TextTrimming = TextTrimming.CharacterEllipsis;
        _message.FontSize = 12;
        _message.Foreground = PanelPalette.ErrorForeground;
        _message.TextWrapping = TextWrapping.Wrap;
        _message.Margin = new Thickness(10, 0, 10, 4);
        _message.Visibility = Visibility.Collapsed;
        _status.FontSize = _statusRight.FontSize = 12;
        _status.Foreground = _statusRight.Foreground = PanelPalette.DescriptionForeground;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;

        var header = new DockPanel { Margin = new Thickness(10, 6, 10, 6), LastChildFill = true };
        DockPanel.SetDock(_title, Dock.Left);
        header.Children.Add(_title);
        header.Children.Add(_path);

        var status = new DockPanel { Margin = new Thickness(10, 3, 10, 4), LastChildFill = true };
        DockPanel.SetDock(_statusRight, Dock.Right);
        status.Children.Add(_statusRight);
        status.Children.Add(_status);
        var statusBorder = new Border { BorderBrush = PanelPalette.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = status };

        _quickSearchText.FontFamily = new FontFamily("Segoe UI");
        _quickSearchText.FontSize = 13;
        _quickSearchText.Foreground = PanelPalette.Foreground;
        _quickSearch.Child = _quickSearchText;
        _quickSearch.Background = PanelPalette.InputBackground;
        _quickSearch.BorderBrush = PanelPalette.FocusOutline;
        _quickSearch.BorderThickness = new Thickness(1);
        _quickSearch.Padding = new Thickness(6, 2, 6, 2);
        _quickSearch.Margin = new Thickness(8);
        _quickSearch.HorizontalAlignment = HorizontalAlignment.Left;
        _quickSearch.VerticalAlignment = VerticalAlignment.Bottom;
        _quickSearch.Visibility = Visibility.Collapsed;

        _body.Children.Add(_list);
        _body.Children.Add(_info);
        _body.Children.Add(_quickSearch);

        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_message, Dock.Top);
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        layout.Children.Add(header);
        layout.Children.Add(_message);
        layout.Children.Add(statusBorder);
        layout.Children.Add(_body);
        Child = layout;

        UpdateTitle();
    }

    /// <summary>A directory was opened (navigation or refresh to another path).</summary>
    public event Action<FilePanel, string>? Navigated;

    /// <summary>Enter on a file: run it through the shell.</summary>
    public event Action<FilePanel, PanelItem>? ExecuteRequested;

    /// <summary>The cursor, selection or listing changed (the info panel follows).</summary>
    public event Action<FilePanel>? Changed;

    public string Side { get; }

    public PanelState State { get; } = new();

    public string CurrentPath => State.Path;

    public string? QuickSearchText { get; private set; }

    /// <summary>Time to the first screen of the last navigation (shown in the info panel).</summary>
    public TimeSpan? FirstScreenTime { get; private set; }

    /// <summary>Time until the whole listing was read and sorted.</summary>
    public TimeSpan? FullListingTime { get; private set; }

    public bool IsQuickSearchActive => QuickSearchText is not null;

    /// <summary>Rows visible in the list (page step).</summary>
    public int PageSize => _list.PageSize;

    /// <summary>Shows the info view for <paramref name="source"/> instead of the file list; null returns to the list.</summary>
    public FilePanel? InfoSource
    {
        get => _info.Source;
        set
        {
            _info.Source = value;
            _info.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
            _list.Visibility = value is null ? Visibility.Visible : Visibility.Collapsed;
            _info.InvalidateVisual();
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            UpdateFocusVisuals();
        }
    }

    public string? Message
    {
        get => _message.Visibility == Visibility.Visible ? _message.Text : null;
        set
        {
            _message.Text = value ?? string.Empty;
            _message.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public void RefreshInfo() => _info.InvalidateVisual();

    /// <summary>Opens a directory. On failure the panel stays where it was and shows the error.</summary>
    public async Task<bool> NavigateAsync(string path, string? focusName = null)
    {
        _loading?.Cancel();
        var loading = new CancellationTokenSource();
        _loading = loading;
        var token = loading.Token;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // First batch quickly (first screen), then the whole listing.
        FileEntry[] first;
        try
        {
            first = await Task.Run(() => DirectoryReader.ReadBatches(path, cancellationToken: token).FirstOrDefault() ?? [], token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (DirectoryReadException ex)
        {
            Message = ex.Error switch
            {
                DirectoryReadError.AccessDenied => $"Access denied: {path}",
                DirectoryReadError.NotFound => $"Not found: {path}",
                _ => ex.InnerException?.Message ?? ex.Message,
            };
            return false;
        }

        var samePath = string.Equals(path, State.Path, StringComparison.OrdinalIgnoreCase);
        Message = null;
        CloseQuickSearch();
        var complete = first.Length < DirectoryReader.DefaultFirstBatchSize;
        if (!complete)
        {
            if (!samePath)
            {
                // Large directory: show the first screen now; a refresh keeps the old listing until the new one is read.
                State.SetListing(path, first, focusName);
                Refresh();
                FirstScreenTime = stopwatch.Elapsed;
            }

            FileEntry[] all;
            try
            {
                all = await Task.Run(() => DirectoryReader.ReadAll(path, token), token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or DirectoryReadException)
            {
                return !token.IsCancellationRequested;
            }

            if (token.IsCancellationRequested)
            {
                return false;
            }

            State.SetListing(path, all, focusName ?? (State.Count > 0 ? State.Current.Name : null));
        }
        else
        {
            State.SetListing(path, first, focusName);
            FirstScreenTime = stopwatch.Elapsed;
        }

        FullListingTime = stopwatch.Elapsed;
        Refresh();
        if (!samePath)
        {
            Watch(path);
            Navigated?.Invoke(this, path);
        }

        return true;
    }

    /// <summary>Ctrl+R: re-reads the directory, keeping cursor and selection.</summary>
    public Task<bool> ReloadAsync() => NavigateAsync(State.Path);

    public void Refresh()
    {
        UpdateTitle();
        UpdateStatus();
        _list.Refresh();
        Changed?.Invoke(this);
    }

    /// <summary>Keys of the panel itself; returns false for keys the window handles.</summary>
    public bool HandleKey(Key key, KeyModifiers modifiers)
    {
        if (IsQuickSearchActive && HandleQuickSearchKey(key, modifiers))
        {
            return true;
        }

        var page = Math.Max(1, _list.PageSize);
        var rows = Math.Max(1, _list.RowsPerColumn);
        var multiColumn = _list.ColumnCount > 1;
        switch (key)
        {
            case Key.Up when modifiers == KeyModifiers.None:
                Move(-1);
                return true;
            case Key.Down when modifiers == KeyModifiers.None:
                Move(1);
                return true;
            case Key.Up when modifiers == KeyModifiers.Shift:
            case Key.Down when modifiers == KeyModifiers.Shift:
                // Far: Shift+arrows mark the entry under the cursor, then move.
                if (!State.Current.IsParent)
                {
                    State.Selection.Toggle(State.Current.Name);
                }

                Move(key == Key.Up ? -1 : 1);
                return true;
            case Key.PageUp when modifiers == KeyModifiers.None:
                Move(-page);
                return true;
            case Key.PageDown when modifiers == KeyModifiers.None:
                Move(page);
                return true;
            case Key.Home when modifiers == KeyModifiers.None:
                MoveTo(0);
                return true;
            case Key.End when modifiers == KeyModifiers.None:
                MoveTo(State.Count - 1);
                return true;
            case Key.Left when modifiers == KeyModifiers.None:
                Move(multiColumn ? -rows : -page);
                return true;
            case Key.Right when modifiers == KeyModifiers.None:
                Move(multiColumn ? rows : page);
                return true;
            case Key.Enter when modifiers == KeyModifiers.None:
                Open(State.Current);
                return true;
            case Key.PageUp when modifiers == KeyModifiers.Control:
                GoToParent();
                return true;
            case Key.PageDown when modifiers == KeyModifiers.Control:
                if (State.Current.Entry.IsDirectory || State.Current.IsParent)
                {
                    Open(State.Current);
                }

                return true;
            case Key.Oem5 when modifiers == KeyModifiers.Control: // Ctrl+\
                _ = NavigateAsync(System.IO.Path.GetPathRoot(State.Path) ?? State.Path);
                return true;
            case Key.Insert when modifiers == KeyModifiers.None:
                State.ToggleCurrentAndMoveDown();
                Refresh();
                return true;
            case Key.Multiply when modifiers == KeyModifiers.None:
                State.Selection.Invert(State.Entries);
                Refresh();
                return true;
            case Key.H when modifiers == KeyModifiers.Control:
                State.SetShowHidden(!State.ShowHidden);
                Refresh();
                return true;
            case Key.R when modifiers == KeyModifiers.Control:
                _ = ReloadAsync();
                return true;
            case Key.F12 when modifiers == KeyModifiers.Shift:
                State.SetSelectedFirst(!State.SelectedFirst);
                Refresh();
                return true;
        }

        if (modifiers == KeyModifiers.Control && ViewModeFor(key) is { } mode)
        {
            State.ViewMode = mode;
            Refresh();
            return true;
        }

        if (modifiers == KeyModifiers.Control && SortModeFor(key) is { } sortMode)
        {
            State.ApplySortKey(sortMode);
            Refresh();
            return true;
        }

        // Alt+letter starts quick search (Far); the letter follows the keyboard layout.
        if (modifiers is KeyModifiers.Alt or (KeyModifiers.Alt | KeyModifiers.Shift) &&
            KeyText.CharOf(key, modifiers.HasFlag(KeyModifiers.Shift)) is { } letter && !char.IsWhiteSpace(letter))
        {
            QuickSearchText = string.Empty;
            AppendQuickSearch(letter.ToString());
            return true;
        }

        return false;
    }

    /// <summary>Printable text typed while quick search is open.</summary>
    public void AppendQuickSearch(string text)
    {
        var candidate = (QuickSearchText ?? string.Empty) + text;
        var found = State.FindNext(candidate, State.CursorIndex);
        if (found < 0 && QuickSearchText is { Length: > 0 })
        {
            // Far keeps the last matching prefix: ignore characters that match nothing.
            return;
        }

        QuickSearchText = candidate;
        if (found >= 0)
        {
            State.SetCursor(found);
        }

        _quickSearchText.Text = "Search: " + QuickSearchText;
        _quickSearch.Visibility = Visibility.Visible;
        Refresh();
    }

    public void CloseQuickSearch()
    {
        QuickSearchText = null;
        _quickSearch.Visibility = Visibility.Collapsed;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        UpdateFocusVisuals();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        CloseQuickSearch();
        UpdateFocusVisuals();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
    }

    private static PanelViewMode? ViewModeFor(Key key) => key switch
    {
        Key.D1 => PanelViewMode.Brief,
        Key.D2 => PanelViewMode.Medium,
        Key.D3 => PanelViewMode.Full,
        Key.D4 => PanelViewMode.Wide,
        Key.D5 => PanelViewMode.Detailed,
        Key.D0 => PanelViewMode.AlternativeFull,
        _ => null,
    };

    private static SortMode? SortModeFor(Key key) => key switch
    {
        Key.F3 => SortMode.Name,
        Key.F4 => SortMode.Extension,
        Key.F5 => SortMode.Modified,
        Key.F6 => SortMode.Size,
        Key.F7 => SortMode.Unsorted,
        Key.F8 => SortMode.Created,
        Key.F9 => SortMode.Accessed,
        _ => null,
    };

    private bool HandleQuickSearchKey(Key key, KeyModifiers modifiers)
    {
        switch (key)
        {
            case Key.Back:
                var text = QuickSearchText!;
                if (text.Length <= 1)
                {
                    CloseQuickSearch();
                }
                else
                {
                    QuickSearchText = text[..^1];
                    _quickSearchText.Text = "Search: " + QuickSearchText;
                }

                return true;
            case Key.Escape:
                CloseQuickSearch();
                return true;
            case Key.Enter when modifiers == KeyModifiers.Control:
            case Key.Enter when modifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                // Ctrl+Enter / Ctrl+Shift+Enter: next / previous match.
                var direction = modifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
                var next = State.FindNext(QuickSearchText!, State.CursorIndex + direction, direction);
                if (next >= 0)
                {
                    State.SetCursor(next);
                    Refresh();
                }

                return true;
            case Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.System:
                return false;
            default:
                // Any other key closes the search and is processed normally (Far).
                CloseQuickSearch();
                return false;
        }
    }

    private void OnItemClicked(int index, int clickCount, bool right)
    {
        Focus();
        State.SetCursor(index);
        if (right)
        {
            if (!State.Current.IsParent)
            {
                State.Selection.Toggle(State.Current.Name);
            }
        }
        else if (clickCount == 2)
        {
            Open(State.Current);
        }

        Refresh();
    }

    private void Move(int delta)
    {
        State.MoveCursor(delta);
        Refresh();
    }

    private void MoveTo(int index)
    {
        State.SetCursor(index);
        Refresh();
    }

    private void Open(PanelItem item)
    {
        if (State.Count == 0)
        {
            return;
        }

        if (item.IsParent)
        {
            GoToParent();
        }
        else if (item.Entry.IsDirectory)
        {
            _ = NavigateAsync(System.IO.Path.Combine(State.Path, item.Entry.Name));
        }
        else
        {
            ExecuteRequested?.Invoke(this, item);
        }
    }

    private void GoToParent()
    {
        var trimmed = State.Path.TrimEnd('\\');
        if (System.IO.Path.GetDirectoryName(trimmed) is { } parent && State.HasParent)
        {
            // The cursor lands on the directory we came from (Far).
            _ = NavigateAsync(parent, System.IO.Path.GetFileName(trimmed));
        }
    }

    private void Watch(string path)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var watcher = new DirectoryWatcher(path);
            watcher.Changed += changes => Dispatcher.BeginInvoke(() => ApplyChanges(path, changes), DispatcherPriority.Background);
            watcher.Start();
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException)
        {
            // Some network paths cannot be watched; polling arrives with the network work.
        }
    }

    private void ApplyChanges(string path, DirectoryChanges changes)
    {
        if (!string.Equals(path, State.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (ListingUpdater.Apply(path, State.RawEntries, changes) is { } updated)
        {
            State.SetListing(path, updated);
            Refresh();
        }
        else
        {
            _ = ReloadAsync();
        }
    }

    private void UpdateTitle()
    {
        var name = State.Path.Length == 0 ? Side : System.IO.Path.GetFileName(State.Path.TrimEnd('\\'));
        _title.Text = (string.IsNullOrEmpty(name) ? State.Path : name).ToUpperInvariant();
        _path.Text = State.Path;
    }

    private void UpdateStatus()
    {
        var culture = CultureInfo.CurrentCulture;
        var (files, directories, bytes) = State.Selection.Summarize(State.Entries);
        if (files + directories > 0)
        {
            _status.Text = $"{files + directories:N0} selected";
            _statusRight.Text = PanelColumns.FormatCompactSize(bytes, culture);
            _status.Foreground = PanelPalette.MarkedForeground;
            return;
        }

        _status.Foreground = PanelPalette.DescriptionForeground;
        if (State.Count == 0)
        {
            _status.Text = "Empty";
            _statusRight.Text = string.Empty;
            return;
        }

        var item = State.Current;
        _status.Text = item.Name;
        _statusRight.Text = item.IsParent ? string.Empty
            : item.Entry.IsDirectory ? item.Entry.LastWriteTimeUtc.ToLocalTime().ToString("g", culture)
            : $"{PanelColumns.FormatCompactSize(item.Entry.Length, culture)}   {item.Entry.LastWriteTimeUtc.ToLocalTime().ToString("g", culture)}";
    }

    private void UpdateFocusVisuals()
    {
        BorderBrush = IsKeyboardFocused ? PanelPalette.FocusOutline : PanelPalette.Border;
        _title.Opacity = _isActive ? 1.0 : 0.6;
        _list.IsActivePanel = _isActive;
        _list.HasPanelFocus = IsKeyboardFocused;
        _list.InvalidateVisual();
    }
}
