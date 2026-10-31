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
    public const double RowHeight = 22;
    private const double HeaderHeight = 22;
    private const double FontSize = 13;
    private const double IconWidth = 20;
    private const double CellPadding = 6;
    private const double SeparatorGrip = 4;
    private const int TextCacheLimit = 4096;

    private static readonly Typeface UiTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface UiTypefaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Geometry FolderGeometry = Geometry.Parse("M1,3 L6,3 L7.5,4.5 L15,4.5 L15,13 L1,13 Z");
    private static readonly Geometry FileGeometry = Geometry.Parse("M3,1.5 L9.5,1.5 L13,5 L13,14.5 L3,14.5 Z M9.5,1.5 L9.5,5 L13,5");
    private static readonly Geometry ParentGeometry = Geometry.Parse("M8,3 L3,8 M8,3 L13,8 M8,3 L8,13");
    private static readonly Geometry LinkGeometry = Geometry.Parse("M9,15 L15,9 M11,9 L15,9 L15,13");

    private readonly Dictionary<(string Text, Brush Brush, bool Bold), FormattedText> _textCache = [];
    private readonly Dictionary<(PanelViewMode Mode, ColumnKind Kind), double> _columnWidths = [];
    private PanelState? _state;
    private int _topIndex;
    private int _hoverIndex = -1;
    private (ColumnSpec Column, double StartX, double StartWidth)? _resizing;

    public FileListView()
    {
        Focusable = false;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    /// <summary>Left or right click on an item: index, click count, right button.</summary>
    public event Action<int, int, bool>? ItemClicked;

    public event Action<SortMode>? HeaderClicked;

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

    public int ColumnCount => _state is null ? 1 : PanelColumns.NameColumnCount(_state.ViewMode);

    /// <summary>Items visible at once (PgUp / PgDn step).</summary>
    public int PageSize => RowsPerColumn * ColumnCount;

    private double ListTop => HasHeader ? HeaderHeight : 0;

    private bool HasHeader => _state is not null && ColumnCount == 1;

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
        var dc = drawingContext;
        dc.DrawRectangle(PanelPalette.Background, null, new Rect(RenderSize));
        if (_state is null)
        {
            return;
        }

        if (_textCache.Count > TextCacheLimit)
        {
            _textCache.Clear();
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var columns = ColumnCount;
        var rows = RowsPerColumn;
        var details = PanelColumns.DetailColumns(_state.ViewMode);
        var columnWidth = ActualWidth / columns;

        if (HasHeader)
        {
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
                DrawItem(dc, index, cell, columns == 1 ? details : [], pixelsPerDip);
            }

            if (column > 0)
            {
                dc.DrawRectangle(PanelPalette.Border, null, new Rect(column * columnWidth, ListTop, 1, rows * RowHeight));
            }
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
            _columnWidths[(_state.ViewMode, resizing.Column.Kind)] = width;
            InvalidateVisual();
            return;
        }

        Cursor = HasHeader && point.Y < HeaderHeight && HeaderSeparatorAt(point.X) is not null ? Cursors.SizeWE : null;
        var hover = HitTest(point);
        if (hover != _hoverIndex)
        {
            _hoverIndex = hover;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        InvalidateVisual();
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
            else if (e.ChangedButton == MouseButton.Left && HeaderColumnAt(point.X)?.SortMode is { } sortMode)
            {
                HeaderClicked?.Invoke(sortMode);
            }

            e.Handled = true;
            return;
        }

        var index = HitTest(point);
        if (index >= 0 && e.ChangedButton is MouseButton.Left or MouseButton.Right)
        {
            ItemClicked?.Invoke(index, e.ClickCount, e.ChangedButton == MouseButton.Right);
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

    private double WidthOf(ColumnSpec column) =>
        _state is not null && _columnWidths.TryGetValue((_state.ViewMode, column.Kind), out var width) ? width : column.DefaultWidth;

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

        // The grip on the left edge of each detail column resizes that column.
        foreach (var (column, columnX, _) in LayoutColumns(PanelColumns.DetailColumns(_state.ViewMode), 0, ActualWidth).Skip(1))
        {
            if (Math.Abs(x - columnX) <= SeparatorGrip)
            {
                return column;
            }
        }

        return null;
    }

    private ColumnSpec? HeaderColumnAt(double x) =>
        _state is null
            ? null
            : LayoutColumns(PanelColumns.DetailColumns(_state.ViewMode), 0, ActualWidth).FirstOrDefault(c => x >= c.X && x < c.X + c.Width).Column;

    private void DrawHeader(DrawingContext dc, IReadOnlyList<ColumnSpec> details, double pixelsPerDip)
    {
        dc.DrawRectangle(PanelPalette.HeaderBackground, null, new Rect(0, 0, ActualWidth, HeaderHeight));
        dc.DrawRectangle(PanelPalette.Border, null, new Rect(0, HeaderHeight - 1, ActualWidth, 1));
        var sort = _state!.Sort;
        foreach (var (column, x, width) in LayoutColumns(details, 0, ActualWidth))
        {
            var title = column.Title;
            if (column.SortMode == sort.Mode && !(column.Kind == ColumnKind.Time))
            {
                title += sort.Descending ? " ↓" : " ↑";
            }

            var left = column.Kind == ColumnKind.Name ? x + CellPadding + IconWidth : x + CellPadding;
            DrawText(dc, title, PanelPalette.DescriptionForeground, left, 0, width - (left - x) - CellPadding, HeaderHeight, column.AlignRight, bold: true, pixelsPerDip);
            if (column.Kind != ColumnKind.Name)
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
        else if (index == _hoverIndex)
        {
            dc.DrawRectangle(PanelPalette.HoverBackground, null, cell);
        }

        var foreground = isMarked ? PanelPalette.MarkedForeground
            : isCursor && IsActivePanel && HasPanelFocus ? PanelPalette.ActiveSelectionForeground
            : PanelPalette.Foreground;
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
                DrawIcon(dc, item, x + CellPadding, cell.Y + 3);
                var nameLeft = x + CellPadding + IconWidth;
                DrawText(dc, item.Name, foreground, nameLeft, cell.Y, width - (nameLeft - x) - CellPadding, RowHeight, false, isMarked, pixelsPerDip);
            }
            else
            {
                var text = PanelColumns.FormatCell(column.Kind, item, state.Culture);
                var brush = isMarked ? PanelPalette.MarkedForeground : PanelPalette.DescriptionForeground;
                DrawText(dc, text, brush, x + CellPadding, cell.Y, width - (2 * CellPadding), RowHeight, column.AlignRight, false, pixelsPerDip);
            }
        }

        if (dimmed)
        {
            dc.Pop();
        }
    }

    private static void DrawIcon(DrawingContext dc, PanelItem item, double x, double y)
    {
        // Placeholder glyphs until the icon themes arrive in stage 5.
        var (geometry, brush) = item.IsParent ? (ParentGeometry, PanelPalette.FolderIcon)
            : item.Entry.IsDirectory ? (FolderGeometry, PanelPalette.FolderIcon)
            : (FileGeometry, PanelPalette.FileIcon);
        dc.PushTransform(new TranslateTransform(x, y));
        dc.DrawGeometry(null, new Pen(brush, 1.2), geometry);
        if (item.Entry.IsReparsePoint)
        {
            dc.DrawGeometry(null, new Pen(PanelPalette.FocusOutline, 1.4), LinkGeometry);
        }

        dc.Pop();
    }

    private void DrawText(DrawingContext dc, string text, Brush brush, double x, double y, double width, double height, bool alignRight, bool bold, double pixelsPerDip)
    {
        if (width <= 4 || text.Length == 0)
        {
            return;
        }

        if (!_textCache.TryGetValue((text, brush, bold), out var formatted))
        {
            formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? UiTypefaceBold : UiTypeface, FontSize, brush, pixelsPerDip)
            {
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            _textCache[(text, brush, bold)] = formatted;
        }

        formatted.MaxTextWidth = width;
        var textX = alignRight ? x + Math.Max(0, width - formatted.WidthIncludingTrailingWhitespace) : x;
        dc.DrawText(formatted, new Point(textX, y + ((height - formatted.Height) / 2)));
    }
}
