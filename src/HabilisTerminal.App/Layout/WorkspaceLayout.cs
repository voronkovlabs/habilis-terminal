using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Input;

namespace HabilisTerminal.App.Layout;

public enum WorkspaceRole
{
    LeftPanel,
    RightPanel,
    Terminal,
    ConsoleTabs,
}

/// <summary>
/// Arranges the two file panels and the terminal without overlap (SPEC §3).
/// Both panels: panels on top, terminal below. One panel: panel and terminal side by side.
/// No panels: terminal fills everything. The terminal area is snapped to whole character cells.
/// </summary>
public sealed class WorkspaceLayout : Panel
{
    public const int MinTerminalRows = 2;
    public const int MinPanelRows = 4;

    public static readonly AttachedProperty<WorkspaceRole> RoleProperty =
        AvaloniaProperty.RegisterAttached<WorkspaceLayout, Control, WorkspaceRole>("Role", WorkspaceRole.Terminal);

    public static readonly StyledProperty<bool> LeftVisibleProperty = AvaloniaProperty.Register<WorkspaceLayout, bool>(nameof(LeftVisible), true);

    public static readonly StyledProperty<bool> RightVisibleProperty = AvaloniaProperty.Register<WorkspaceLayout, bool>(nameof(RightVisible), true);

    public static readonly StyledProperty<int> TerminalRowsProperty = AvaloniaProperty.Register<WorkspaceLayout, int>(nameof(TerminalRows), 10);

    public static readonly StyledProperty<int> OverlayRowsProperty = AvaloniaProperty.Register<WorkspaceLayout, int>(nameof(OverlayRows));

    public static readonly StyledProperty<double> SinglePanelRatioProperty = AvaloniaProperty.Register<WorkspaceLayout, double>(nameof(SinglePanelRatio), 0.5);

    public static readonly StyledProperty<double> SplitRatioProperty = AvaloniaProperty.Register<WorkspaceLayout, double>(nameof(SplitRatio), 0.5);

    public static readonly StyledProperty<Size> CellSizeProperty = AvaloniaProperty.Register<WorkspaceLayout, Size>(nameof(CellSize), new Size(8, 16));

    public static readonly StyledProperty<bool> ShowsTabStripProperty = AvaloniaProperty.Register<WorkspaceLayout, bool>(nameof(ShowsTabStrip));

    static WorkspaceLayout()
    {
        AffectsParentMeasure<WorkspaceLayout>(RoleProperty);
        AffectsMeasure<WorkspaceLayout>(LeftVisibleProperty, RightVisibleProperty, TerminalRowsProperty, OverlayRowsProperty,
            SinglePanelRatioProperty, SplitRatioProperty, CellSizeProperty, ShowsTabStripProperty);
    }

    public WorkspaceLayout()
    {
        // Tunnelling: the splitters win over the children under them.
        AddHandler(PointerMovedEvent, OnPreviewPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPreviewPointerReleased, RoutingStrategies.Tunnel);
    }

    public static WorkspaceRole GetRole(Control element) => element.GetValue(RoleProperty);

    public static void SetRole(Control element, WorkspaceRole value) => element.SetValue(RoleProperty, value);

    public bool LeftVisible
    {
        get => GetValue(LeftVisibleProperty);
        set => SetValue(LeftVisibleProperty, value);
    }

    public bool RightVisible
    {
        get => GetValue(RightVisibleProperty);
        set => SetValue(RightVisibleProperty, value);
    }

    public int TerminalRows
    {
        get => GetValue(TerminalRowsProperty);
        set => SetValue(TerminalRowsProperty, value);
    }

    /// <summary>
    /// Both panels shown: rows the terminal's top edge rises over the panels, which stay where they are underneath
    /// (issue #82, experiment). Clamped to the top of the workspace.
    /// </summary>
    public int OverlayRows
    {
        get => GetValue(OverlayRowsProperty);
        set => SetValue(OverlayRowsProperty, value);
    }

    /// <summary>The most <see cref="OverlayRows"/> can be now: the panels' height in whole rows.</summary>
    public int MaxOverlayRows
    {
        get
        {
            var (left, _, _) = ComputeRects(Bounds.Size, overlay: false);
            return LeftVisible && RightVisible && CellSize.Height > 0 ? (int)Math.Floor(left.Height / CellSize.Height) : 0;
        }
    }

    public double SinglePanelRatio
    {
        get => GetValue(SinglePanelRatioProperty);
        set => SetValue(SinglePanelRatioProperty, value);
    }

    /// <summary>Width share of the left panel when both panels are visible (Ctrl+Left / Ctrl+Right, splitter).</summary>
    public double SplitRatio
    {
        get => GetValue(SplitRatioProperty);
        set => SetValue(SplitRatioProperty, value);
    }

    /// <summary>Terminal character cell size in device-independent pixels.</summary>
    public Size CellSize
    {
        get => GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }

    /// <summary>The tabs below the console, while there are several consoles: it grows upwards, they stay (issue #119).</summary>
    public bool ShowsTabStrip
    {
        get => GetValue(ShowsTabStripProperty);
        set => SetValue(ShowsTabStripProperty, value);
    }

    private double StripHeight => ShowsTabStrip ? Widgets.ConsoleTabStrip.StripHeight : 0;

    private static bool IsEmpty(Rect rect) => rect.Width <= 0 && rect.Height <= 0;

    private Rect SlotOf(Control child, Rect left, Rect right, Rect terminal) => GetRole(child) switch
    {
        WorkspaceRole.LeftPanel => left,
        WorkspaceRole.RightPanel => right,
        WorkspaceRole.ConsoleTabs => ShowsTabStrip ? new Rect(terminal.Left, terminal.Bottom, terminal.Width, StripHeight) : default,
        _ => terminal,
    };

    /// <summary>A console not in front keeps its last size, so its shell gets no resize until it is shown.</summary>
    private static bool IsHiddenConsole(Control child) => GetRole(child) == WorkspaceRole.Terminal && child.IsVisible == false;

    protected override Size MeasureOverride(Size availableSize)
    {
        using var measure = Core.Diagnostics.Perf.Measure("layout.measure");
        var size = new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

        // Each child is measured in its own slot. Measured with the whole workspace, a panel whose status line holds a
        // long file name wanted more than its slot, WPF arranged it at that width and clipped it, and the list columns
        // jumped as the cursor moved.
        var (left, right, terminal) = ComputeRects(size);
        foreach (var child in Children)
        {
            if (!IsHiddenConsole(child))
            {
                var rect = SlotOf(child, left, right, terminal);
                child.Measure(IsEmpty(rect) ? new Size(0, 0) : rect.Size);
            }
        }

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        using var measure = Core.Diagnostics.Perf.Measure("layout.arrange");
        var (left, right, terminal) = ComputeRects(finalSize);

        foreach (var child in Children)
        {
            if (IsHiddenConsole(child))
            {
                continue;
            }

            var rect = SlotOf(child, left, right, terminal);

            // A console is never empty, and those of tabs not in front stay hidden (issue #119).
            if (GetRole(child) != WorkspaceRole.Terminal)
            {
                child.IsVisible = !(IsEmpty(rect));
            }

            child.Arrange(IsEmpty(rect) ? new Rect(0, 0, 0, 0) : rect);

            // Under the console grown over the panels (issue #82), the panels' covered part is clipped away: the
            // terminal is snapped to whole cells and would leave the panels showing in the few pixels beside it.
            child.Clip = GetRole(child) is WorkspaceRole.LeftPanel or WorkspaceRole.RightPanel && !IsEmpty(rect) && OverlayRows > 0 && LeftVisible && RightVisible
                ? new RectangleGeometry(new Rect(0, 0, rect.Width, Math.Max(0, terminal.Top - rect.Top)))
                : null;
        }

        return finalSize;
    }

    private const double Grip = 4;
    private Splitter _dragging = Splitter.None;

    private enum Splitter
    {
        None,

        /// <summary>Between the two panels.</summary>
        Panels,

        /// <summary>Between panels (top) and terminal (bottom).</summary>
        TerminalTop,

        /// <summary>Between a single panel and the terminal beside it.</summary>
        TerminalSide,
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_dragging != Splitter.None)
        {
            Drag(point);
            e.Handled = true;
            return;
        }

        Cursor = SplitterAt(point) switch
        {
            Splitter.TerminalTop => SizeNorthSouth,
            Splitter.Panels or Splitter.TerminalSide => SizeWestEast,
            _ => null,
        };
    }

    private static readonly Cursor SizeNorthSouth = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor SizeWestEast = new(StandardCursorType.SizeWestEast);

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var splitter = SplitterAt(e.GetPosition(this));
        if (splitter != Splitter.None)
        {
            _dragging = splitter;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging != Splitter.None)
        {
            _dragging = Splitter.None;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private Splitter SplitterAt(Point point)
    {
        var (left, right, terminal) = ComputeRects(Bounds.Size);
        if (LeftVisible && RightVisible)
        {
            if (Math.Abs(point.Y - terminal.Top) <= Grip)
            {
                return Splitter.TerminalTop;
            }

            return point.Y < terminal.Top && Math.Abs(point.X - right.Left) <= Grip ? Splitter.Panels : Splitter.None;
        }

        if (LeftVisible ^ RightVisible)
        {
            var edge = LeftVisible ? left.Right : right.Left;
            return Math.Abs(point.X - edge) <= Grip ? Splitter.TerminalSide : Splitter.None;
        }

        return Splitter.None;
    }

    private void Drag(Point point)
    {
        var size = Bounds.Size;
        switch (_dragging)
        {
            case Splitter.Panels:
                SplitRatio = Math.Clamp(point.X / size.Width, 0.1, 0.9);
                break;
            case Splitter.TerminalTop:
                TerminalRows = Math.Max(MinTerminalRows, (int)Math.Round((size.Height - point.Y - StripHeight) / CellSize.Height));
                break;
            case Splitter.TerminalSide:
                var ratio = LeftVisible ? point.X / size.Width : 1 - (point.X / size.Width);
                SinglePanelRatio = Math.Clamp(ratio, 0.1, 0.9);
                break;
        }
    }

    private (Rect Left, Rect Right, Rect Terminal) ComputeRects(Size size, bool overlay = true)
    {
        var cell = CellSize;
        var strip = StripHeight;

        if (!LeftVisible && !RightVisible)
        {
            return (default, default, new Rect(0, 0, size.Width, Math.Max(0, size.Height - strip)));
        }

        if (LeftVisible && RightVisible)
        {
            // The strip comes out of the panels, so a console keeps its size when the strip appears or goes.
            var maxRows = Math.Max(MinTerminalRows, (int)Math.Floor((size.Height - strip - (MinPanelRows * cell.Height)) / cell.Height));
            var rows = Math.Clamp(TerminalRows, MinTerminalRows, maxRows);
            var terminalHeight = Math.Min(size.Height, rows * cell.Height);
            var panelsHeight = Math.Max(0, size.Height - terminalHeight - strip);
            var half = Math.Floor(size.Width * Math.Clamp(SplitRatio, 0.1, 0.9));
            var rise = overlay ? Math.Min(panelsHeight, Math.Clamp(OverlayRows, 0, int.MaxValue) * cell.Height) : 0;

            // Grown all the way, no sliver of the panels stays above it.
            if (overlay && cell.Height > 0 && OverlayRows >= Math.Floor(panelsHeight / cell.Height))
            {
                rise = panelsHeight;
            }

            return (
                new Rect(0, 0, half, panelsHeight),
                new Rect(half, 0, size.Width - half, panelsHeight),
                new Rect(0, panelsHeight - rise, size.Width, terminalHeight + rise));
        }

        // One panel: the terminal takes the place of the hidden one, snapped to whole columns.
        var panelWidth = Math.Floor(size.Width * Math.Clamp(SinglePanelRatio, 0.1, 0.9));
        var columns = Math.Max(1, Math.Floor((size.Width - panelWidth) / cell.Width));
        var terminalWidth = Math.Min(size.Width, columns * cell.Width);
        panelWidth = size.Width - terminalWidth;

        return LeftVisible
            ? (new Rect(0, 0, panelWidth, size.Height), default, new Rect(panelWidth, 0, terminalWidth, Math.Max(0, size.Height - strip)))
            : (default, new Rect(terminalWidth, 0, panelWidth, size.Height), new Rect(0, 0, terminalWidth, Math.Max(0, size.Height - strip)));
    }
}
