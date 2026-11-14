using System.IO;
using System.Globalization;
using HabilisTerminal.App.Reporting;
using HabilisTerminal.Core.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using HabilisTerminal.App.Input;
using HabilisTerminal.App.Plugins;
using HabilisTerminal.App.Theming;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Panels;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.App.Panels;

/// <summary>
/// One file panel: header with the path, the file list (or the info view), status line and quick search.
/// Loads directories in the background (first batch at once, the rest after), follows changes through
/// <see cref="DirectoryWatcher"/>, and handles the panel's own keys (SPEC §5).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Panels live as long as the window; the quick view releases its file when hidden.")]
public sealed partial class FilePanel : Border
{
    private readonly FileListView _list = new();
    private readonly InfoPanelView _info = new();
    private readonly QuickView _quickView = new() { IsVisible = false };
    private readonly ContentControl _pluginViewHost = new() { Focusable = false, IsVisible = false, IsTabStop = false };
    private int _quickViewVersion;
    private FilePanel? _quickViewSource;
    private readonly TextBlock _title = new();
    private readonly TextBlock _path = new();
    private readonly TextBlock _filterClear = new();
    private Border _filterClearTarget = null!;

    /// <summary>The box below the list is the quick filter (Ctrl+Alt+letter), not the quick search (Alt+letter).</summary>
    private bool _quickFilter;
    private readonly TextBlock _message = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _statusRight = new();
    private readonly Border _quickSearch = new();
    private readonly TextBlock _quickSearchText = new();
    private readonly Grid _body = new();
    private CancellationTokenSource? _loading;
    private FolderCount? _counting;
    private DirectoryWatcher? _watcher;
    private FoundListing? _found;
    private Border? _statusBorder;
    private bool _isActive;
    private bool _autoSizing;
    private int _sizesRepaintQueued;
    private int _shownFolderSizes;
    private readonly Dictionary<string, int> _hardLinks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hardLinksRead = new(StringComparer.OrdinalIgnoreCase);
    private string? _hardLinksFolder;
    private bool _readsHardLinks;
    private CancellationTokenSource? _hardLinkScan;
    private static FolderSizeWorker? s_sizeWorker;

    public FilePanel(string side)
    {
        Side = side;
        State.FolderSizes = FolderSizes;
        SizeWorker.Updated += OnFolderSizesUpdated;

        // Another panel's Ctrl+R or a file operation may have dropped sizes this panel shows (an ancestor, say).
        FolderSizes.Invalidated += _ => Avalonia.Threading.Dispatcher.UIThread.Post(RequestFolderSizes, DispatcherPriority.Background);
        _list.VisibleRangeChanged += (_, _) =>
        {
            UpdateVisibleFolders();
            ReadHardLinks();
        };
        _list.HardLinks = _hardLinks;
        Focusable = true;
        ;
        Background = PanelPalette.Background;
        BorderThickness = new Thickness(1);
        BorderBrush = PanelPalette.Border;

        _list.State = State;
        _list.ItemClicked += OnItemClicked;
        _list.ContextMenuRequested += OnContextMenuRequested;
        _list.CustomHeaderClicked += key =>
        {
            State.ApplyCustomSortKey(key);
            Refresh();
        };
        _list.HeaderClicked += mode =>
        {
            State.ApplySortKey(mode);
            Refresh();
        };
        _info.IsVisible = false;

        _title.FontFamily = _path.FontFamily = _status.FontFamily = _statusRight.FontFamily = _message.FontFamily = new FontFamily("Segoe UI");
        _title.FontSize = 11;
        _title.FontWeight = FontWeight.SemiBold;
        _title.Foreground = PanelPalette.Foreground;
        _title.Margin = new Thickness(0, 0, 8, 0);
        _path.FontSize = 12;
        _path.Foreground = PanelPalette.DescriptionForeground;
        _path.TextTrimming = TextTrimming.CharacterEllipsis;
        _message.FontSize = 12;
        _message.Foreground = PanelPalette.ErrorForeground;
        _message.TextWrapping = TextWrapping.Wrap;
        _message.Margin = new Thickness(10, 0, 10, 4);
        _message.IsVisible = false;
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
        _statusBorder = statusBorder;

        _quickSearchText.FontFamily = new FontFamily("Segoe UI");
        _quickSearchText.FontSize = 13;
        _quickSearchText.Foreground = PanelPalette.Foreground;
        _quickSearchText.VerticalAlignment = VerticalAlignment.Center;

        // The quick filter's box stays while the filter is in force, with a cross that clears it (issues #201, #205).
        _filterClear.FontFamily = new FontFamily("Segoe UI");
        _filterClear.Text = "✕";
        _filterClear.FontSize = 12;
        _filterClear.VerticalAlignment = VerticalAlignment.Center;
        _filterClear.Foreground = PanelPalette.Foreground;
        _filterClear.ToolTip = "Clear the filter (Esc)";

        // A larger target than the glyph, taken on the press: the panel takes the focus on the press too.
        _filterClearTarget = new Border
        {
            Child = _filterClear,
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 0, 2, 0),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
            IsVisible = false,
        };
        _filterClearTarget.AddHandler(PointerPressedEvent, (_, e) =>
        {
            ClearQuickFilter();
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var boxRow = new StackPanel { Orientation = Orientation.Horizontal };
        boxRow.Children.Add(_quickSearchText);
        boxRow.Children.Add(_filterClearTarget);
        _quickSearch.Child = boxRow;
        _quickSearch.Background = PanelPalette.InputBackground;
        _quickSearch.BorderBrush = PanelPalette.FocusOutline;
        _quickSearch.BorderThickness = new Thickness(1);
        _quickSearch.Padding = new Thickness(6, 2, 6, 2);
        _quickSearch.Margin = new Thickness(8);
        _quickSearch.HorizontalAlignment = HorizontalAlignment.Left;
        _quickSearch.VerticalAlignment = VerticalAlignment.Bottom;
        _quickSearch.IsVisible = false;

        _body.Children.Add(_list);
        _body.Children.Add(_pluginViewHost);
        _body.Children.Add(_info);
        _body.Children.Add(_quickView);
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

    /// <summary>
    /// Enter on a file a plugin claims (anyFormat false; the window runs it when it turns out not to be that plugin's)
    /// or Ctrl+PgDn on any file (anyFormat true): go inside it, in a plugin panel (issues #51, #148).
    /// </summary>
    public event Action<FilePanel, PanelItem, bool>? FileAsFolderRequested;

    /// <summary>A plugin opens this file name as a folder; set by the window.</summary>
    internal Func<string, bool>? OpensAsFolder { get; set; }

    /// <summary>The cursor, selection or listing changed (the info panel follows).</summary>
    public event Action<FilePanel>? Changed;

    /// <summary>
    /// Right click, Shift+F10 or the Menu key (issue #41): show the shell context menu for these names in the current
    /// folder (empty: the folder's background) at this point in physical screen pixels.
    /// </summary>
    public event Action<FilePanel, IReadOnlyList<string>, PixelPoint>? ShellMenuRequested;

    public string Side { get; }

    /// <summary>Folder sizes shared by both panels, F3 and the selection totals, kept for the app's lifetime (SPEC §6).</summary>
    public static FolderSizeCache FolderSizes { get; } = new();

    /// <summary>The <c>panels.folderSizes</c> setting: size folders in the background on local fixed drives.</summary>
    public static bool AutoFolderSizes { get; set; } = true;

    private static FolderSizeWorker SizeWorker => s_sizeWorker ??= new FolderSizeWorker(FolderSizes);

    /// <summary>The app changed something at <paramref name="path"/> (copy, move, delete, editor save): its sizes are stale.</summary>
    public static void InvalidateFolderSizes(string path) => FolderSizes.Invalidate(path);

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
            UpdateBody();
            _info.InvalidateVisual();
        }
    }

    /// <summary>Shows Ctrl+Q quick view of <paramref name="value"/>'s current file instead of the list; null returns to the list.</summary>
    public FilePanel? QuickViewSource
    {
        get => _quickViewSource;
        set
        {
            _quickViewSource = value;
            UpdateBody();
            if (value is null)
            {
                _quickView.Dispose();
            }
            else
            {
                UpdateQuickView();
            }
        }
    }

    /// <summary>What the panel body shows: info or quick view of the other panel, a plugin's own view, or the list.</summary>
    /// <summary>The list is hidden under an info panel or a quick view: keys must not act on what cannot be seen (issue #128).</summary>
    public bool ShowsOtherContent => InfoSource is not null || _quickViewSource is not null;

    public void ScrollQuickView(Key key) => _quickView.Scroll(key);

    private void UpdateBody()
    {
        var otherContent = InfoSource is not null || _quickViewSource is not null;
        _info.IsVisible = !(InfoSource is null);
        _quickView.IsVisible = !(_quickViewSource is null);
        _pluginViewHost.IsVisible = !otherContent && _pluginViewHost.Content is not null;
        _list.IsVisible = !otherContent && _pluginViewHost.Content is null;
        UpdateStatus();
    }

    /// <summary>Follows the source panel's cursor.</summary>
    public void UpdateQuickView()
    {
        if (_quickViewSource is not { } source || source.State.Count == 0)
        {
            return;
        }

        UpdateStatus();
        var version = ++_quickViewVersion;
        if (source.IsPlugin)
        {
            if (source.Provider is HabilisTerminal.Plugins.IQuickViewEntry viewer && source.EntryOf(source.State.Current) is { } entry)
            {
                _ = ShowPluginQuickViewAsync(source.Plugin!, viewer, entry, version);
            }
            else
            {
                _quickView.ShowText(null);
            }

            return;
        }

        var item = source.State.Current;
        if (item.IsParent)
        {
            _quickView.Show(null, false, null);
            return;
        }

        var path = System.IO.Path.Combine(source.CurrentPath, item.Name);
        _quickView.Show(path, item.Entry.IsDirectory, source.State.FolderSizeOf(item.Name) is { IsFinal: true } size ? size.Bytes : null);
    }

    /// <summary>The plugin's quick view text (issue #70); an answer that arrives after the cursor moved on is dropped.</summary>
    private async Task ShowPluginQuickViewAsync(Core.Plugins.LoadedPlugin plugin, HabilisTerminal.Plugins.IQuickViewEntry viewer, HabilisTerminal.Plugins.PanelEntry entry, int version)
    {
        string? text;
        try
        {
            text = await viewer.QuickViewAsync(entry, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            text = Plugins.PluginManager.Describe(plugin, ex);
        }

        if (version == _quickViewVersion)
        {
            _quickView.ShowText(text);
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
        get => _message.IsVisible ? _message.Text : null;
        set
        {
            _message.Text = value ?? string.Empty;
            _message.IsVisible = !(string.IsNullOrEmpty(value));
        }
    }

    public void RefreshInfo() => _info.InvalidateVisual();

    /// <summary>Re-reads brushes after a theme change.</summary>
    public void ApplyTheme()
    {
        Background = PanelPalette.Background;
        _title.Foreground = PanelPalette.Foreground;
        _path.Foreground = PanelPalette.DescriptionForeground;
        _filterClear.Foreground = PanelPalette.Foreground;
        _message.Foreground = PanelPalette.ErrorForeground;
        _statusRight.Foreground = PanelPalette.DescriptionForeground;
        _quickSearchText.Foreground = PanelPalette.Foreground;
        _quickSearch.Background = PanelPalette.InputBackground;
        _quickSearch.BorderBrush = PanelPalette.FocusOutline;
        if (_statusBorder is not null)
        {
            _statusBorder.BorderBrush = PanelPalette.Border;
        }

        _list.ApplyTheme();
        _info.InvalidateVisual();
        _quickView.ApplyTheme();
        UpdateStatus();
        UpdateFocusVisuals();
    }

    /// <summary>Opens a directory. On failure the panel stays where it was and shows the error.</summary>
    public async Task<bool> NavigateAsync(string path, string? focusName = null)
    {
        using var measure = Perf.Enabled ? Perf.MeasureAsync("panel.navigate", Perf.DriveKind(path)) : default;
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

        // A folder replaces a plugin panel (Alt+F1, history, the shell's cd).
        ClosePlugin();

        // Search results left for a real listing, even of their own root.
        var leavingFound = _found is not null;
        _found = null;
        var samePath = !leavingFound && string.Equals(path, State.Path, StringComparison.OrdinalIgnoreCase);
        if (!samePath)
        {
            SizeWorker.Cancel(this); // leaving the folder stops its counting
        }

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
                RequestFolderSizes();
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
        if (Perf.Enabled)
        {
            Perf.RecordMilestone("panel.listing", stopwatch.Elapsed.TotalMilliseconds, Perf.CountRange(State.Count));
        }

        Refresh();
        RequestFolderSizes();
        if (!samePath)
        {
            Watch(path);
            Navigated?.Invoke(this, path);
        }

        return true;
    }

    /// <summary>Re-reads the directory, keeping cursor and selection.</summary>
    public Task<bool> ReloadAsync() => _found is not null ? RefreshFoundAsync() : NavigateAsync(State.Path);

    /// <summary>Alt+F7's Panel (issue #198): the search results, named by their path below the root. Leaving the panel ends it.</summary>
    public async Task ShowFoundAsync(string root, string title, IReadOnlyList<string> names)
    {
        ClosePlugin();
        _loading?.Cancel();
        SizeWorker.Cancel(this);
        var entries = await Task.Run(() => ReadFound(root, names));
        _found = new FoundListing(title, [.. names]);
        _watcher?.Dispose(); // changes below the root are not followed; Ctrl+R checks the files again
        _watcher = null;
        Message = null;
        CloseQuickSearch();
        State.Selection.Clear();
        State.SetListing(root, entries, hasParent: true);
        Refresh();
        RequestFolderSizes();
    }

    /// <summary>After an operation or Ctrl+R: what is gone leaves the list, what changed shows its new size.</summary>
    private async Task<bool> RefreshFoundAsync()
    {
        var found = _found!;
        var root = State.Path;
        var entries = await Task.Run(() => ReadFound(root, found.Names));
        if (_found == found)
        {
            State.SetListing(root, entries, hasParent: true);
            Refresh();
        }

        return true;
    }

    private static FileEntry[] ReadFound(string root, IReadOnlyList<string> names) =>
        [.. names.Select(name => DirectoryReader.ReadEntry(root, name)).Where(e => e is not null).Select(e => e!.Value)];

    private sealed record FoundListing(string Title, IReadOnlyList<string> Names);

    /// <summary>Ctrl+R: re-reads the directory and counts its folder sizes again.</summary>
    public Task<bool> RefreshAsync()
    {
        if (IsPlugin)
        {
            return ListPluginAsync();
        }

        if (State.Path.Length > 0)
        {
            FolderSizes.Invalidate(State.Path);
        }

        return ReloadAsync();
    }

    /// <summary>
    /// Asks the background counter for the sizes of this listing's folders (visible ones first), or stops it when
    /// the setting is off or the drive is not a local fixed one.
    /// </summary>
    public void RequestFolderSizes()
    {
        RestartHardLinks();
        var auto = !IsPlugin && AutoFolderSizes && FolderSizeWorker.IsAutoCountable(State.Path);
        if (auto != _autoSizing)
        {
            _autoSizing = auto;
            _list.AutoSizedFolders = auto;
            _list.InvalidateVisual();
        }

        if (!auto)
        {
            SizeWorker.Cancel(this);
            return;
        }

        SizeWorker.Request(this, State.Entries.Where(IsSizable).Select(e => System.IO.Path.Combine(State.Path, e.Name)));
        UpdateVisibleFolders();
    }

    private static bool IsSizable(FileEntry entry) => entry.IsDirectory && !entry.IsLink;

    /// <summary>The listing changed: what is on screen is read again. Asking about the drive here keeps it out of drawing.</summary>
    private void RestartHardLinks()
    {
        if (!string.Equals(_hardLinksFolder, State.Path, StringComparison.OrdinalIgnoreCase))
        {
            _hardLinksFolder = State.Path;
            _hardLinks.Clear();
        }

        _hardLinksRead.Clear();
        _readsHardLinks = !IsPlugin && FolderSizeWorker.IsAutoCountable(State.Path);
        ReadHardLinks();
    }

    /// <summary>
    /// Finds files on screen with several names (issue #180). That takes one call per file, so only for visible rows, off
    /// the UI thread, and only on local fixed drives, where it is cheap.
    /// </summary>
    private async void ReadHardLinks()
    {
        _hardLinkScan?.Cancel();
        if (!_readsHardLinks)
        {
            return;
        }

        var (top, count) = _list.VisibleRange;
        var wanted = new List<string>();
        for (var i = top; i < Math.Min(State.Count, top + count); i++)
        {
            var item = State.ItemAt(i);
            if (!item.IsParent && !item.Entry.IsDirectory && !item.Entry.IsLink && _hardLinksRead.Add(item.Name))
            {
                wanted.Add(item.Name);
            }
        }

        if (wanted.Count == 0)
        {
            return;
        }

        var folder = State.Path;
        using var scan = new CancellationTokenSource();
        _hardLinkScan = scan;
        var token = scan.Token;
        var counts = await Task.Run(() =>
        {
            var found = new List<(string Name, int Count)>();
            foreach (var name in wanted)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                found.Add((name, LinkInfo.HardLinkCount(System.IO.Path.Combine(folder, name))));
            }

            return found;
        }, CancellationToken.None);
        if (_hardLinkScan == scan)
        {
            _hardLinkScan = null;
        }

        if (!string.Equals(folder, State.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var changed = false;
        foreach (var name in wanted.Skip(counts.Count))
        {
            _hardLinksRead.Remove(name); // not reached: asked again when next on screen
        }

        foreach (var (name, number) in counts)
        {
            if (number > 1)
            {
                changed |= _hardLinks.GetValueOrDefault(name) != number;
                _hardLinks[name] = number;
            }
            else
            {
                changed |= _hardLinks.Remove(name);
            }
        }

        if (changed)
        {
            _list.InvalidateVisual();
            UpdateStatus();
        }

        // Stopped part way by a scroll: what it did not reach may still be on screen.
        if (counts.Count < wanted.Count)
        {
            ReadHardLinks();
        }
    }

    private void UpdateVisibleFolders()
    {
        if (!_autoSizing)
        {
            return;
        }

        var (top, count) = _list.VisibleRange;
        var visible = new List<string>();
        for (var i = top; i < Math.Min(State.Count, top + count); i++)
        {
            var item = State.ItemAt(i);
            if (!item.IsParent && IsSizable(item.Entry))
            {
                visible.Add(System.IO.Path.Combine(State.Path, item.Name));
            }
        }

        SizeWorker.SetVisible(this, visible);
    }

    /// <summary>On the worker thread, at most a few times a second: repaint the sizes on the UI thread.</summary>
    private void OnFolderSizesUpdated()
    {
        if (Interlocked.Exchange(ref _sizesRepaintQueued, 1) == 0)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _sizesRepaintQueued = 0;
                var shown = _list.VisibleFolderSizes();
                if (shown != _shownFolderSizes)
                {
                    _shownFolderSizes = shown;
                    _list.InvalidateVisual();
                }

                UpdateStatus();
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>After a font or icon change: rows may have a new height.</summary>
    public void InvalidatePanel()
    {
        _list.InvalidateMeasure();
        _list.InvalidateVisual();
        Refresh();
    }

    public void Refresh()
    {
        if (_counting is { } counting && (counting.Path != State.Path || counting.CursorName != CursorName()))
        {
            counting.Cancel.Cancel(); // the cursor moved: Far stops counting too
        }

        UpdateTitle();
        UpdateQuickBox();
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

        // Esc on a filtered panel shows everything again (issue #201).
        if (key == Key.Escape && modifiers == KeyModifiers.None && State.NameFilter is not null)
        {
            ClearQuickFilter();
            return true;
        }

        if (key == Key.Escape && modifiers == KeyModifiers.None && HidePluginCursor())
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
            // Far: the same row of the neighbouring column, clamped to the first / last file; one column acts as Home / End.
            case Key.Left when modifiers == KeyModifiers.None:
                if (multiColumn)
                {
                    Move(-rows);
                }
                else
                {
                    MoveTo(0);
                }

                return true;
            case Key.Right when modifiers == KeyModifiers.None:
                if (multiColumn)
                {
                    Move(rows);
                }
                else
                {
                    MoveTo(State.Count - 1);
                }

                return true;
            case Key.Enter when modifiers == KeyModifiers.None:
                Open(State.Current);
                return true;
            case Key.PageUp when modifiers == KeyModifiers.Control:
                GoToParent();
                return true;
            case Key.PageDown when modifiers == KeyModifiers.Control:
                if (State.Current.Entry.IsDirectory || State.Current.IsParent || IsPlugin)
                {
                    Open(State.Current, intoPackage: true);
                }
                else if (State.Count > 0)
                {
                    FileAsFolderRequested?.Invoke(this, State.Current, true);
                }

                return true;
            case Key.Oem5 when modifiers == KeyModifiers.Control: // Ctrl+\
                if (!IsPlugin)
                {
                    _ = NavigateAsync(System.IO.Path.GetPathRoot(State.Path) ?? State.Path);
                }

                return true;
            case Key.Insert when modifiers == KeyModifiers.None:
                State.ToggleCurrentAndMoveDown();
                Refresh();
                return true;
            case Key.F10 when modifiers == KeyModifiers.Shift:
            case Key.Apps when modifiers == KeyModifiers.None:
                // The selection when there is one, else the item under the cursor; ".." means the folder itself.
                RequestShellMenu(
                    State.Selection.Count > 0 ? [.. State.Selection.Names]
                    : State.Count == 0 || State.Current.IsParent ? []
                    : [State.Current.Name],
                    _list.CursorMenuPoint());
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
                _ = RefreshAsync();
                return true;
            case Key.F12 when modifiers == KeyModifiers.Shift:
                State.SetSelectedFirst(!State.SelectedFirst);
                Refresh();
                return true;
        }

        if (IsPlugin && modifiers == KeyModifiers.Control && key is >= Key.D1 and <= Key.D9)
        {
            SetPluginLayout(key - Key.D1);
            return true;
        }

        if (IsPlugin && modifiers == KeyModifiers.Control && key == Key.D0 && SetPluginAlternativeLayout())
        {
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

        // Ctrl+Alt+letter starts the quick filter (issue #201).
        if (CtrlAltLetter(key, modifiers) is { } first)
        {
            CloseQuickSearch();
            _quickFilter = true;
            QuickSearchText = string.Empty;
            AppendQuickSearch(first.ToString());
            return true;
        }

        // Alt+letter starts quick search (Far).
        if (AltLetter(key, modifiers) is { } letter)
        {
            QuickSearchText = string.Empty;
            AppendQuickSearch(letter.ToString());
            return true;
        }

        return false;
    }

    /// <summary>
    /// A key typed without Alt or Ctrl that will arrive as text input (a character in the current layout, or a packet
    /// from an IME or injected Unicode, which has no key). Quick search takes it as text and stays open.
    /// </summary>
    public bool IsQuickSearchTyping(Key key, KeyModifiers modifiers) =>
        IsQuickSearchActive && modifiers is KeyModifiers.None or KeyModifiers.Shift &&
        (key is Key.None or Key.ImeProcessed || KeyText.CharOf(key, modifiers == KeyModifiers.Shift) is { } typed && !char.IsControl(typed));

    /// <summary>The character of Alt+key or Alt+Shift+key in the current keyboard layout, or null.</summary>
    private static char? AltLetter(Key key, KeyModifiers modifiers) =>
        modifiers is KeyModifiers.Alt or (KeyModifiers.Alt | KeyModifiers.Shift) &&
        KeyText.CharOf(key, modifiers.HasFlag(KeyModifiers.Shift)) is { } letter && !char.IsWhiteSpace(letter)
            ? letter
            : null;

    /// <summary>The character of Ctrl+Alt+key (or with Shift) in the current keyboard layout, or null.</summary>
    private static char? CtrlAltLetter(Key key, KeyModifiers modifiers) =>
        modifiers is (KeyModifiers.Control | KeyModifiers.Alt) or (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift) &&
        KeyText.CharOf(key, modifiers.HasFlag(KeyModifiers.Shift)) is { } letter && !char.IsWhiteSpace(letter) && !char.IsControl(letter)
            ? letter
            : null;

    /// <summary>Clears the quick filter and closes its box; the cursor stays on the same name.</summary>
    public void ClearQuickFilter()
    {
        if (_quickFilter)
        {
            CloseQuickSearch();
        }

        if (State.NameFilter is null)
        {
            return;
        }

        State.SetNameFilter(null);
        Refresh();
    }

    private void SetQuickFilter(string text)
    {
        QuickSearchText = text;
        State.SetNameFilter(text);
        _quickSearchText.Text = "Filter: " + text;
        _quickSearch.IsVisible = true;
        Refresh();
    }

    /// <summary>
    /// The box below the list: open while typing a search or a filter, and kept while a filter is in force. Only the
    /// filter has the cross; a search started over a filter shows the search until it closes.
    /// </summary>
    private void UpdateQuickBox()
    {
        if (QuickSearchText is not null)
        {
            _filterClearTarget.IsVisible = _quickFilter;
            _quickSearch.IsVisible = true;
        }
        else if (State.NameFilter is { } filter)
        {
            _quickSearchText.Text = "Filter: " + filter;
            _filterClearTarget.IsVisible = true;
            _quickSearch.IsVisible = true;
        }
        else
        {
            _filterClearTarget.IsVisible = false;
            _quickSearch.IsVisible = false;
        }
    }

    /// <summary>Printable text typed while quick search is open.</summary>
    public void AppendQuickSearch(string text)
    {
        if (_quickFilter)
        {
            SetQuickFilter((QuickSearchText ?? string.Empty) + text);
            return;
        }

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
            RevealPluginCursor(found);
            State.SetCursor(found);
        }

        _quickSearchText.Text = "Search: " + QuickSearchText;
        _quickSearch.IsVisible = true;
        Refresh();
    }

    public void CloseQuickSearch()
    {
        _quickFilter = false;
        QuickSearchText = null;
        UpdateQuickBox();
    }

    /// <summary>
    /// The panel has the keys while the Win32 focus stays in the console (issue #90): drawn as focused, and quick
    /// search closes when the keys go elsewhere.
    /// </summary>
    public bool HasKeys
    {
        get => _hasKeys;
        set
        {
            if (_hasKeys == value)
            {
                return;
            }

            _hasKeys = value;
            if (!value)
            {
                CloseQuickSearch();
            }

            UpdateFocusVisuals();
        }
    }

    private bool _hasKeys;

    /// <summary>Drawn as having the keys although it has none: a command has just started (issue #118).</summary>
    public bool HoldsFrame
    {
        get => _holdsFrame;
        set
        {
            if (_holdsFrame != value)
            {
                _holdsFrame = value;
                UpdateFocusVisuals();
            }
        }
    }

    private bool _holdsFrame;

    /// <summary>The frame's colour while the panel has the keys: the console tab's (issue #120); null is the theme's.</summary>
    public Avalonia.Media.IBrush? FrameBrush
    {
        get => _frameBrush;
        set
        {
            _frameBrush = value;
            UpdateFocusVisuals();
        }
    }

    private Avalonia.Media.IBrush? _frameBrush;

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        UpdateFocusVisuals();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        if (!HasKeys)
        {
            CloseQuickSearch();
        }

        UpdateFocusVisuals();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
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
        if (_quickFilter)
        {
            return HandleQuickFilterKey(key, modifiers);
        }

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
                // Alt still held: the next letter appends, as in Far (issue #39). Plain letters arrive as text input.
                if (AltLetter(key, modifiers) is { } letter)
                {
                    AppendQuickSearch(letter.ToString());
                    return true;
                }

                // Any other key closes the search and is processed normally (Far).
                CloseQuickSearch();
                return false;
        }
    }

    /// <summary>
    /// While the filter's box is open: Backspace edits, Esc clears the filter, Enter keeps it and closes the box, and any
    /// other key closes the box, keeps the filter and is processed normally.
    /// </summary>
    private bool HandleQuickFilterKey(Key key, KeyModifiers modifiers)
    {
        switch (key)
        {
            case Key.Back:
                var text = QuickSearchText!;
                if (text.Length <= 1)
                {
                    ClearQuickFilter();
                }
                else
                {
                    SetQuickFilter(text[..^1]);
                }

                return true;
            case Key.Escape:
                ClearQuickFilter();
                return true;
            case Key.Enter when modifiers == KeyModifiers.None:
                CloseQuickSearch();
                Refresh();
                return true;
            case Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.System:
                return false;
            default:
                if (CtrlAltLetter(key, modifiers) is { } letter)
                {
                    AppendQuickSearch(letter.ToString());
                    return true;
                }

                CloseQuickSearch();
                return false;
        }
    }

    private void OnItemClicked(int index, int clickCount)
    {
        Focus();
        RevealPluginCursor(index);
        State.SetCursor(index);
        if (Keyboard.Modifiers == MainWindow.MarkModifier)
        {
            // Ctrl+click marks, as in Explorer: the right button opens the context menu (issue #41). Cmd+click on a Mac,
            // as in the Finder, whose keyboard has no Insert either (#272).
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

    /// <summary>
    /// Right click (issue #41): on a marked item the menu covers the whole selection, on another item only that item
    /// (the cursor moves to it), on empty space or ".." the folder's background.
    /// </summary>
    private void OnContextMenuRequested(int index, PixelPoint screen)
    {
        Focus();
        IReadOnlyList<string> names = [];
        if (index >= 0)
        {
            State.SetCursor(index);
            Refresh();
            var item = State.Current;
            if (!item.IsParent)
            {
                names = State.Selection.Contains(item.Name) ? [.. State.Selection.Names] : [item.Name];
            }
        }

        RequestShellMenu(names, screen);
    }

    private void RequestShellMenu(IReadOnlyList<string> names, PixelPoint screen)
    {
        if (IsPlugin)
        {
            Message = "The Windows context menu is not available in a plugin panel.";
            return;
        }

        if (!string.IsNullOrEmpty(State.Path))
        {
            ShellMenuRequested?.Invoke(this, names, screen);
        }
    }

    private void Move(int delta)
    {
        if (RevealPluginCursor())
        {
            Refresh();
            return;
        }

        State.MoveCursor(delta);
        Refresh();
    }

    private void MoveTo(int index)
    {
        RevealPluginCursor(index);
        State.SetCursor(index);
        Refresh();
    }

    private void Open(PanelItem item, bool intoPackage = false)
    {
        if (State.Count == 0)
        {
            return;
        }

        if (IsPlugin)
        {
            _ = PluginOpenAsync(item);
            return;
        }

        if (item.IsParent)
        {
            GoToParent();
        }
        else if (item.Entry.IsDirectory && !intoPackage && OperatingSystem.IsMacOS() && Core.FileSystem.MacPackages.IsPackage(item.Name))
        {
            ExecuteRequested?.Invoke(this, item); // a program or a document package, as the Finder opens it (#274)
        }
        else if (item.Entry.IsDirectory)
        {
            _ = NavigateAsync(System.IO.Path.Combine(State.Path, item.Entry.Name));
        }
        else if (OpensAsFolder?.Invoke(item.Name) == true)
        {
            FileAsFolderRequested?.Invoke(this, item, false);
        }
        else
        {
            ExecuteRequested?.Invoke(this, item);
        }
    }

    private void GoToParent()
    {
        if (_found is not null)
        {
            _ = NavigateAsync(State.Path); // out of the results, into the folder they were found in
            return;
        }

        if (IsPlugin)
        {
            _ = PluginUpAsync();
            return;
        }

        var trimmed = State.Path.TrimEndSeparator();
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
            watcher.Changed += changes => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyChanges(path, changes), DispatcherPriority.Background);
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
        if (_found is not null || !string.Equals(path, State.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (ListingUpdater.Apply(path, State.RawEntries, changes) is { } updated)
        {
            State.SetListing(path, updated);
            Refresh();
            RequestFolderSizes();
        }
        else
        {
            _ = ReloadAsync();
        }
    }

    private void UpdateTitle()
    {
        if (Provider is { } provider)
        {
            _title.Text = PluginManager.Guard(Plugin!, () => provider.Title, Plugin!.Name).ToUpperInvariant();
            _path.Text = State.Path;
            return;
        }

        if (_found is { } found)
        {
            _title.Text = found.Title.ToUpperInvariant();
            _path.Text = $"{State.Path}  ·  {found.Names.Count:N0} found  ·  .. leaves";
            return;
        }

        var name = State.Path.Length == 0 ? Side : System.IO.Path.GetFileName(State.Path.TrimEndSeparator());
        _title.Text = (string.IsNullOrEmpty(name) ? State.Path : name).ToUpperInvariant();
        _path.Text = State.Path;
    }

    /// <summary>
    /// F3 on folders: counts their files and bytes one after another in the background; the status line shows the
    /// running totals. Moving the cursor, leaving the directory or a new count stops it; folders already counted keep
    /// their totals.
    /// </summary>
    public async Task CountFoldersAsync(IReadOnlyList<string> names)
    {
        _counting?.Cancel.Cancel();
        var counting = new FolderCount(State.Path, CursorName(), new CancellationTokenSource());
        _counting = counting;
        var unreadable = 0;
        try
        {
            foreach (var name in names)
            {
                counting.Name = name;
                counting.Totals = new DirectorySize.Result(0, 0, 0, 0);
                UpdateStatus();
                var path = System.IO.Path.Combine(counting.Path, name);
                counting.FullPath = path;
                var stamp = FolderSizes.Stamp;
                SizeWorker.Skip(path);
                var progress = new Progress<DirectorySize.Result>(totals =>
                {
                    // Reports queue up on the dispatcher; one from a cancelled count must not repaint.
                    if (_counting == counting && !counting.Cancel.IsCancellationRequested)
                    {
                        counting.Totals = totals;
                        FolderSizes.Set(path, totals, isFinal: false, startStamp: stamp);
                        UpdateStatus();
                        _list.InvalidateVisual();
                    }
                });
                var result = await Task.Run(() => DirectorySize.Compute(path, new Throttled(progress), counting.Cancel.Token), counting.Cancel.Token);
                FolderSizes.Set(path, result, isFinal: true, startStamp: stamp);
                counting.FullPath = null;
                unreadable += result.Inaccessible;
                _list.Refresh();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (counting.FullPath is { } stopped)
            {
                FolderSizes.RemoveRunning(stopped);
            }

            counting.Cancel.Dispose();
            if (_counting == counting)
            {
                _counting = null;
                UpdateStatus();
            }

            if (State.Path == counting.Path)
            {
                RequestFolderSizes(); // a stopped F3 count goes back to the background counter
            }
        }

        if (unreadable > 0 && State.Path == counting.Path)
        {
            Message = $"{unreadable} folder(s) could not be read; the totals are partial.";
        }
    }

    private string? CursorName() => State.Count > 0 ? State.Current.Name : null;

    private sealed class FolderCount(string path, string? cursorName, CancellationTokenSource cancel)
    {
        public string Path { get; } = path;

        public string? CursorName { get; } = cursorName;

        public CancellationTokenSource Cancel { get; } = cancel;

        public string Name { get; set; } = string.Empty;

        /// <summary>The folder being counted now; null between folders.</summary>
        public string? FullPath { get; set; }

        public DirectorySize.Result Totals { get; set; } = new(0, 0, 0, 0);
    }

    /// <summary>Passes on at most ten reports a second: a tree of small folders reports thousands.</summary>
    private sealed class Throttled(IProgress<DirectorySize.Result> inner) : IProgress<DirectorySize.Result>
    {
        private long _next;

        public void Report(DirectorySize.Result value)
        {
            var now = Environment.TickCount64;
            if (now >= _next)
            {
                _next = now + 100;
                inner.Report(value);
            }
        }
    }

    private void UpdateStatus()
    {
        var culture = CultureInfo.CurrentCulture;

        // The status line belongs to what is shown, not to the list hidden under it (issue #128).
        if (ShowsOtherContent)
        {
            _status.Foreground = PanelPalette.DescriptionForeground;
            _status.Text = _quickViewSource is { State.Count: > 0 } source && !source.State.Current.IsParent ? source.State.Current.Name : string.Empty;
            _statusRight.Text = string.Empty;
            return;
        }

        if (_counting is { } counting)
        {
            _status.Foreground = PanelPalette.DescriptionForeground;
            _status.Text = $"Counting {counting.Name}…";
            _statusRight.Text = FormatTotals(counting.Totals.Files, counting.Totals.Bytes, culture);
            return;
        }

        var (items, files, bytes) = State.SummarizeSelection();
        if (items > 0)
        {
            _status.Text = $"{items:N0} selected";
            _statusRight.Text = FormatTotals(files, bytes, culture);
            _status.Foreground = PanelPalette.MarkedForeground;
            return;
        }

        _status.Foreground = PanelPalette.DescriptionForeground;
        if (!CursorVisible)
        {
            _status.Text = string.Empty;
            _statusRight.Text = string.Empty;
            return;
        }

        // A filter that leaves only "..": the status line says why the panel is empty (issue #201).
        if (State.NameFilter is { } filter && State.Count == (State.HasParent ? 1 : 0))
        {
            _status.Text = $"No names contain \u201C{filter}\u201D";
            _statusRight.Text = string.Empty;
            return;
        }

        if (State.Count == 0)
        {
            _status.Text = "Empty";
            _statusRight.Text = string.Empty;
            return;
        }

        var item = State.Current;
        _status.Text = item.Name;
        _statusRight.Text = item.IsParent ? string.Empty
            : item.Entry.IsDirectory && State.FolderSizeOf(item.Name) is { } size
                ? (size.IsPartial ? SizeFormat.AtLeast : string.Empty) + FormatTotals(size.Totals.Files, size.Bytes, culture)
            : item.Entry.IsDirectory ? Time(item.Entry)
            : $"{HardLinkText(item.Name)}{PanelColumns.FormatCompactSize(item.Entry.Length, culture)}   {Time(item.Entry)}".TrimEnd();

        string HardLinkText(string name) => _hardLinks.TryGetValue(name, out var names) ? $"{names} hard links   " : string.Empty;

        // A plugin row without a time (GPU monitor, archive folders) shows none.
        string Time(FileEntry entry) => entry.LastWriteTimeUtc == DateTime.MinValue ? string.Empty : entry.LastWriteTimeUtc.ToLocalTime().ToString("g", culture);
    }

    private static string FormatTotals(int files, long bytes, CultureInfo culture) =>
        $"{files.ToString("N0", culture)} {(files == 1 ? "file" : "files")}   {PanelColumns.FormatCompactSize(bytes, culture)}";

    private void UpdateFocusVisuals()
    {
        var focused = IsFocused || HasKeys || HoldsFrame;
        BorderBrush = focused ? FrameBrush ?? PanelPalette.FocusOutline : PanelPalette.Border;
        _title.Opacity = _isActive ? 1.0 : 0.6;
        _list.IsActivePanel = _isActive;
        _list.HasPanelFocus = focused;
        _list.ScrollbarBrush = BorderBrush;
        _list.InvalidateVisual();
    }
}
