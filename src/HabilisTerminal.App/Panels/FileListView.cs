using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HabilisTerminal.App.Theming;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Panels;

namespace HabilisTerminal.App.Panels;

/// <summary>
/// Custom-drawn file list (SPEC §11): draws only the visible rows with <see cref="DrawingContext"/>, implements
/// every Far view mode including multi-column Brief, and looks like a VS Code list (22 px rows, 13 px Segoe UI).
/// The view never changes <see cref="PanelState"/> on its own except scrolling; clicks are reported as events.
/// </summary>
public sealed class FileListView : FrameworkElement
{
    private const double HeaderHeight = 22;
    private const double IconWidth = 20;
    private const double CellPadding = 6;
    private const double SeparatorGrip = 4;
    private const int TextCacheLimit = 4096;

    private static Typeface UiTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static Typeface UiTypefaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static double FontSize = 13;
    private static int FontVersion;
    private int _renderedFontVersion;
    private bool _firstTextOfRender;
    private static readonly Geometry LinkGeometry = Geometry.Parse("M9,15 L15,9 M11,9 L15,9 L15,13");

    private readonly Dictionary<(string Text, Brush Brush, bool Bold), FormattedText> _textCache = [];

    public static double RowHeight { get; private set; } = 22;

    /// <summary>The <c>panels.fontFamily</c> / <c>panels.fontSize</c> settings; rows grow with the font. Callers re-layout.</summary>
    public static void SetFont(string family, double size)
    {
        var fontFamily = new FontFamily(string.IsNullOrWhiteSpace(family) ? "Segoe UI" : family);
        UiTypeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        UiTypefaceBold = new Typeface(fontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        FontSize = size;
        RowHeight = Math.Round(size * 22 / 13);
        FontVersion++;
    }
    private readonly Dictionary<(string Mode, string Column), double> _columnWidths = [];
    private PanelState? _state;
    private int _topIndex;
    private (ColumnSpec Column, double StartX, double StartWidth)? _resizing;
    private (int Top, int Count) _reportedRange = (-1, -1);

    public FileListView()
    {
        IconProvider.SystemIconLoaded += InvalidateVisual;
        Focusable = false;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    /// <summary>Left click on an item: index, click count.</summary>
    public event Action<int, int>? ItemClicked;

    /// <summary>Right click: the item index (-1 for empty space) and the click point in physical screen pixels.</summary>
    public event Action<int, Point>? ContextMenuRequested;

    public event Action<SortMode>? HeaderClicked;

    /// <summary>A click on a plugin panel's own column header: its key (issue #49).</summary>
    public event Action<string>? CustomHeaderClicked;

    /// <summary>The visible rows changed (scroll, resize, view mode): first index and count. Raised while rendering.</summary>
    public event Action<int, int>? VisibleRangeChanged;

    /// <summary>The panel sizes folders in the background: folders not sized yet show an empty size cell.</summary>
    public bool AutoSizedFolders { get; set; }

    /// <summary>
    /// Stands for the folder sizes on screen. A count ticks several times a second, mostly for folders not on screen or
    /// in the other panel, and redrawing for those changes nothing (performance reports #156, #162).
    /// </summary>
    public int VisibleFolderSizes()
    {
        if (_state is null)
        {
            return 0;
        }

        var hash = default(HashCode);
        var (top, count) = VisibleRange;
        for (var index = top; index < Math.Min(_state.Count, top + count); index++)
        {
            var item = _state.ItemAt(index);
            if (!item.IsParent && item.Entry.IsDirectory)
            {
                hash.Add(_state.FolderSizeOf(item.Name));
            }
        }

        return hash.ToHashCode();
    }

    /// <summary>First visible index and the number of visible slots.</summary>
    public (int Top, int Count) VisibleRange => (_topIndex, PageSize);

    public PanelState? State
    {
        get => _state;
        set
        {
            _state = value;
            _topIndex = 0;
            Refresh();
        }
    }

    /// <summary>The panel is the active one (cursor drawn as selection).</summary>
    public bool IsActivePanel { get; set; }

    /// <summary>The panel has keyboard focus (cursor drawn with the focus outline).</summary>
    public bool HasPanelFocus { get; set; }

    public int RowsPerColumn => Math.Max(1, (int)Math.Floor((ActualHeight - ListTop) / RowHeight));

    public int ColumnCount => _state is null ? 1 : PanelColumns.NameColumnCount(_state);

    /// <summary>Items visible at once (PgUp / PgDn step).</summary>
    public int PageSize => RowsPerColumn * ColumnCount;

    private double ListTop => HasHeader ? HeaderHeight : 0;

    /// <summary>Table modes, and name-column modes with detail columns (Medium, issue #40); Brief has none.</summary>
    private bool HasHeader =>
        _state is not null && (ColumnCount == 1 || PanelColumns.DetailColumns(_state).Count > 0);

    /// <summary>Drops cached text (it holds brushes of the previous theme) and repaints.</summary>
    public void ApplyTheme()
    {
        _textCache.Clear();
        InvalidateVisual();
    }

    /// <summary>Redraws after the state changed and keeps the cursor visible.</summary>
    public void Refresh()
    {
        EnsureCursorVisible();
        InvalidateVisual();
    }

    public void EnsureCursorVisible()
    {
        if (_state is null || ActualHeight <= 0)
        {
            return;
        }

        var cursor = _state.CursorIndex;
        var rows = RowsPerColumn;
        if (ColumnCount == 1)
        {
            if (cursor < _topIndex)
            {
                _topIndex = cursor;
            }
            else if (cursor >= _topIndex + rows)
            {
                _topIndex = cursor - rows + 1;
            }
        }
        else
        {
            // Multi-column: scroll by whole columns, like Far.
            if (cursor < _topIndex)
            {
                _topIndex = cursor / rows * rows;
            }
            else if (cursor >= _topIndex + PageSize)
            {
                _topIndex = ((cursor / rows) - ColumnCount + 1) * rows;
            }
        }

        _topIndex = Math.Clamp(_topIndex, 0, Math.Max(0, _state.Count - 1));
    }

    public int HitTest(Point point)
    {
        if (_state is null || point.Y < ListTop)
        {
            return -1;
        }

        var row = (int)((point.Y - ListTop) / RowHeight);
        var column = ColumnCount == 1 ? 0 : (int)(point.X / (ActualWidth / ColumnCount));
        if (row >= RowsPerColumn || column >= ColumnCount)
        {
            return -1;
        }

        var index = _topIndex + (column * RowsPerColumn) + row;
        return index < _state.Count ? index : -1;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _textCache.Clear();
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        EnsureCursorVisible();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        using var measure = Core.Diagnostics.Perf.Measure("panel.render");
        var dc = drawingContext;
        dc.DrawRectangle(PanelPalette.Background, null, new Rect(RenderSize));
        if (_state is null)
        {
            return;
        }

        if (_textCache.Count > TextCacheLimit || _renderedFontVersion != FontVersion)
        {
            _textCache.Clear();
            _renderedFontVersion = FontVersion;
        }

        _firstTextOfRender = true;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var columns = ColumnCount;
        var rows = RowsPerColumn;
        IReadOnlyList<ColumnSpec> details;
        using (Core.Diagnostics.Perf.Measure("panel.render.columns"))
        {
            details = PanelColumns.DetailColumns(_state);
        }

        var columnWidth = ActualWidth / columns;

        if (HasHeader)
        {
            using var header = Core.Diagnostics.Perf.Measure("panel.render.header");
            DrawHeader(dc, details, pixelsPerDip);
        }

        for (var column = 0; column < columns; column++)
        {
            for (var row = 0; row < rows; row++)
            {
                var index = _topIndex + (column * rows) + row;
                if (index >= _state.Count)
                {
                    break;
                }

                var cell = new Rect(column * columnWidth, ListTop + (row * RowHeight), columnWidth, RowHeight);
                DrawItem(dc, index, cell, details, pixelsPerDip);
            }

            if (column > 0)
            {
                dc.DrawRectangle(PanelPalette.Border, null, new Rect(column * columnWidth, ListTop, 1, rows * RowHeight));
            }
        }

        if (VisibleRange != _reportedRange)
        {
            using var range = Core.Diagnostics.Perf.Measure("panel.render.visible-range");
            _reportedRange = VisibleRange;
            VisibleRangeChanged?.Invoke(_reportedRange.Top, _reportedRange.Count);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_resizing is { } resizing && _state is not null)
        {
            // Dragging the left edge of a detail column: moving left widens it.
            var width = Math.Max(24, resizing.StartWidth + (resizing.StartX - point.X));
            _columnWidths[WidthKey(resizing.Column)] = width;
            InvalidateVisual();
            return;
        }

        // No hover highlight on rows, as in Far: moving the mouse over a panel changes nothing (issue #65).
        Cursor = HasHeader && point.Y < HeaderHeight && HeaderSeparatorAt(point.X) is not null ? Cursors.SizeWE : null;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        var point = e.GetPosition(this);
        if (HasHeader && point.Y < HeaderHeight && _state is not null)
        {
            if (e.ChangedButton == MouseButton.Left && HeaderSeparatorAt(point.X) is { } column)
            {
                _resizing = (column, point.X, WidthOf(column));
                CaptureMouse();
            }
            else if (e.ChangedButton == MouseButton.Left && HeaderColumnAt(point.X) is { Kind: ColumnKind.Custom, Key: { } key })
            {
                CustomHeaderClicked?.Invoke(key);
            }
            else if (e.ChangedButton == MouseButton.Left && HeaderColumnAt(point.X)?.SortMode is { } sortMode)
            {
                HeaderClicked?.Invoke(sortMode);
            }

            e.Handled = true;
            return;
        }

        var index = HitTest(point);
        if (index >= 0 && e.ChangedButton == MouseButton.Left)
        {
            ItemClicked?.Invoke(index, e.ClickCount);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            // The menu opens on button up, as in Explorer.
            e.Handled = true;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_resizing is not null)
        {
            _resizing = null;
            ReleaseMouseCapture();
        }

        var point = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Right && !(HasHeader && point.Y < HeaderHeight))
        {
            ContextMenuRequested?.Invoke(HitTest(point), PointToScreen(point));
            e.Handled = true;
        }
    }

    /// <summary>Where a keyboard-opened context menu goes: below the start of the cursor row, in physical screen pixels.</summary>
    public Point CursorMenuPoint()
    {
        if (_state is null || !IsVisible)
        {
            return default;
        }

        EnsureCursorVisible();
        var rows = RowsPerColumn;
        var offset = Math.Max(0, _state.CursorIndex - _topIndex);
        var column = ColumnCount == 1 ? 0 : Math.Min(ColumnCount - 1, offset / rows);
        var row = ColumnCount == 1 ? Math.Min(rows - 1, offset) : offset % rows;
        var x = (column * (ActualWidth / ColumnCount)) + (2 * CellPadding) + IconWidth;
        var y = ListTop + ((row + 1) * RowHeight);
        return PointToScreen(new Point(x, y));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_state is null)
        {
            return;
        }

        var step = ColumnCount == 1 ? 3 : RowsPerColumn;
        var maxTop = Math.Max(0, _state.Count - PageSize);
        _topIndex = Math.Clamp(_topIndex + (e.Delta > 0 ? -step : step), 0, maxTop);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Widths are kept per view mode, and per column key in plugin panels (issue #49).</summary>
    private (string Mode, string Column) WidthKey(ColumnSpec column) =>
        (_state?.Extension is null ? _state?.ViewMode.ToString() ?? string.Empty : "plugin", column.Key ?? column.Kind.ToString());

    private double WidthOf(ColumnSpec column) =>
        _state is not null && _columnWidths.TryGetValue(WidthKey(column), out var width) ? width : Math.Round(column.DefaultWidth * FontSize / 13);

    /// <summary>Detail column layout from the right edge; the name column takes the rest.</summary>
    private List<(ColumnSpec Column, double X, double Width)> LayoutColumns(IReadOnlyList<ColumnSpec> details, double left, double width)
    {
        var result = new List<(ColumnSpec, double, double)>();
        var right = left + width;
        for (var i = details.Count - 1; i >= 0; i--)
        {
            var columnWidth = WidthOf(details[i]);
            right -= columnWidth;
            result.Insert(0, (details[i], right, columnWidth));
        }

        result.Insert(0, (PanelColumns.Name, left, Math.Max(0, right - left)));
        return result;
    }

    private ColumnSpec? HeaderSeparatorAt(double x)
    {
        if (_state is null)
        {
            return null;
        }

        // The grip on the left edge of each detail column resizes that column (in every name column alike).
        foreach (var (column, columnX, _) in HeaderLayout(PanelColumns.DetailColumns(_state)))
        {
            if (column.Kind != ColumnKind.Name && Math.Abs(x - columnX) <= SeparatorGrip)
            {
                return column;
            }
        }

        return null;
    }

    private ColumnSpec? HeaderColumnAt(double x) =>
        _state is null
            ? null
            : HeaderLayout(PanelColumns.DetailColumns(_state)).FirstOrDefault(c => x >= c.X && x < c.X + c.Width).Column;

    /// <summary>Header cells over every name column: one set in table modes, Name | Size twice in Medium.</summary>
    private IEnumerable<(ColumnSpec Column, double X, double Width)> HeaderLayout(IReadOnlyList<ColumnSpec> details)
    {
        var columns = ColumnCount;
        var columnWidth = ActualWidth / columns;
        return Enumerable.Range(0, columns).SelectMany(column => LayoutColumns(details, column * columnWidth, columnWidth));
    }

    private void DrawHeader(DrawingContext dc, IReadOnlyList<ColumnSpec> details, double pixelsPerDip)
    {
        dc.DrawRectangle(PanelPalette.HeaderBackground, null, new Rect(0, 0, ActualWidth, HeaderHeight));
        dc.DrawRectangle(PanelPalette.Border, null, new Rect(0, HeaderHeight - 1, ActualWidth, 1));
        var sort = _state!.Sort;
        foreach (var (column, x, width) in HeaderLayout(details))
        {
            var title = column.Title;
            var customSort = _state.Extension?.SortKey;
            var sorted = customSort is not null
                ? column.Key == customSort
                : column.SortMode == sort.Mode && column.Kind != ColumnKind.Time;
            if (sorted)
            {
                title += (customSort is not null ? _state.Extension!.SortDescending : sort.Descending) ? " ↓" : " ↑";
            }

            var left = column.Kind == ColumnKind.Name ? x + CellPadding + IconWidth : x + CellPadding;
            DrawText(dc, title, PanelPalette.DescriptionForeground, left, 0, width - (left - x) - CellPadding, HeaderHeight, column.AlignRight, bold: true, pixelsPerDip);
            if (column.Kind != ColumnKind.Name || x > 0)
            {
                dc.DrawRectangle(PanelPalette.Border, null, new Rect(x, 4, 1, HeaderHeight - 8));
            }
        }
    }

    private void DrawItem(DrawingContext dc, int index, Rect cell, IReadOnlyList<ColumnSpec> details, double pixelsPerDip)
    {
        var state = _state!;
        var item = state.ItemAt(index);
        var isCursor = index == state.CursorIndex;
        var isMarked = !item.IsParent && state.Selection.Contains(item.Name);

        if (isCursor && IsActivePanel)
        {
            dc.DrawRectangle(HasPanelFocus ? PanelPalette.ActiveSelectionBackground : PanelPalette.InactiveSelectionBackground, null, cell);
            if (HasPanelFocus)
            {
                dc.DrawRectangle(null, new Pen(PanelPalette.FocusOutline, 1), new Rect(cell.X + 0.5, cell.Y + 0.5, cell.Width - 1, cell.Height - 1));
            }
        }

        // A plugin row may carry a tone (issue #49): it colours the row unless it is marked or under the cursor.
        Brush? tone = null;
        if (!item.IsParent && state.Extension is { } extension)
        {
            using var plugin = Core.Diagnostics.Perf.Measure("panel.render.plugin");
            tone = PanelPalette.Tone(extension.Tone(item.Entry));
        }

        var cursorFocused = isCursor && IsActivePanel && HasPanelFocus;
        var foreground = isMarked ? PanelPalette.MarkedForeground
            : cursorFocused ? PanelPalette.ActiveSelectionForeground
            : tone ?? PanelPalette.Foreground;
        var dimmed = !item.IsParent && (item.Entry.IsHidden || item.Entry.IsSystem);
        if (dimmed)
        {
            dc.PushOpacity(0.6);
        }

        var columns = details.Count == 0
            ? [(PanelColumns.Name, cell.X, cell.Width)]
            : LayoutColumns(details, cell.X, cell.Width);

        foreach (var (column, x, width) in columns)
        {
            if (column.Kind == ColumnKind.Name)
            {
                using (Core.Diagnostics.Perf.Measure("panel.render.icon"))
                {
                    DrawIcon(dc, item, x + CellPadding, cell.Y + 3);
                }

                var nameLeft = x + CellPadding + IconWidth;
                DrawText(dc, item.Name, foreground, nameLeft, cell.Y, width - (nameLeft - x) - CellPadding, RowHeight, false, isMarked, pixelsPerDip);
            }
            else
            {
                FolderSize? folderSize;
                using (Core.Diagnostics.Perf.Measure("panel.render.folder-size"))
                {
                    folderSize = item.Entry.IsDirectory && !item.IsParent ? state.FolderSizeOf(item.Name) : null;
                }

                string text;
                if (column.Kind == ColumnKind.Custom)
                {
                    using var plugin = Core.Diagnostics.Perf.Measure("panel.render.plugin");
                    text = item.IsParent ? string.Empty : state.Extension?.Cell(item.Entry, column.Key!)?.Text ?? string.Empty;
                }
                else
                {
                    text = PanelColumns.FormatCell(column.Kind, item, state.Culture, folderSize, AutoSizedFolders);
                }

                // On the focused cursor row the details use the selection colour, like the name: the description
                // colour can match the selection background (the Far theme's teal, issue #42).
                var brush = isMarked ? PanelPalette.MarkedForeground
                    : cursorFocused ? PanelPalette.ActiveSelectionForeground
                    : tone ?? PanelPalette.DescriptionForeground;

                // A running total is dimmed until the folder is fully counted.
                var counting = folderSize is { IsFinal: false } && column.Kind is ColumnKind.Size or ColumnKind.CompactSize;
                if (counting)
                {
                    dc.PushOpacity(0.5);
                }

                DrawText(dc, text, brush, x + CellPadding, cell.Y, width - (2 * CellPadding), RowHeight, column.AlignRight, false, pixelsPerDip);
                if (counting)
                {
                    dc.Pop();
                }
            }
        }

        if (dimmed)
        {
            dc.Pop();
        }
    }

    private void DrawIcon(DrawingContext dc, PanelItem item, double x, double y)
    {
        // A plugin row may name a host icon (issue #69).
        if (!item.IsParent && _state!.Extension?.Icon(item.Entry) is { } icon && IconProvider.DrawNamed(dc, icon, x, y))
        {
            return;
        }

        IconProvider.Draw(dc, item, _state!.Path, x, y);
        if (!item.IsParent && item.Entry.IsReparsePoint)
        {
            dc.PushTransform(new TranslateTransform(x, y));
            dc.DrawGeometry(PanelPalette.Background, new Pen(PanelPalette.FocusOutline, 1.4), LinkGeometry);
            dc.Pop();
        }
    }
    private void DrawText(DrawingContext dc, string text, Brush brush, double x, double y, double width, double height, bool alignRight, bool bold, double pixelsPerDip)
    {
        if (width <= 4 || text.Length == 0)
        {
            return;
        }

        if (!_textCache.TryGetValue((text, brush, bold), out var formatted))
        {
            using var measure = Core.Diagnostics.Perf.Measure("panel.render.new-text");
            formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? UiTypefaceBold : UiTypeface, FontSize, brush, pixelsPerDip)
            {
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            _textCache[(text, brush, bold)] = formatted;
        }

        // Most of the waiting in the reports is in the header, which is drawn first: this tells its text from being first.
        using var drawing = Core.Diagnostics.Perf.Measure(_firstTextOfRender ? "panel.render.first-text" : "panel.render.text");
        _firstTextOfRender = false;

        // Setting it, even to the same value, throws away the layout the cache is there to keep (issue #136).
        if (formatted.MaxTextWidth != width)
        {
            formatted.MaxTextWidth = width;
        }

        var textX = alignRight ? x + Math.Max(0, width - formatted.WidthIncludingTrailingWhitespace) : x;
        dc.DrawText(formatted, new Point(textX, y + ((height - formatted.Height) / 2)));
    }
}
