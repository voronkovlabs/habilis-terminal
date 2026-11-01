using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HabilisTerminal.App.Layout;

public enum WorkspaceRole
{
    LeftPanel,
    RightPanel,
    Terminal,
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

    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached(
        "Role", typeof(WorkspaceRole), typeof(WorkspaceLayout),
        new FrameworkPropertyMetadata(WorkspaceRole.Terminal, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty LeftVisibleProperty = DependencyProperty.Register(
        nameof(LeftVisible), typeof(bool), typeof(WorkspaceLayout),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RightVisibleProperty = DependencyProperty.Register(
        nameof(RightVisible), typeof(bool), typeof(WorkspaceLayout),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty TerminalRowsProperty = DependencyProperty.Register(
        nameof(TerminalRows), typeof(int), typeof(WorkspaceLayout),
        new FrameworkPropertyMetadata(10, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SinglePanelRatioProperty = DependencyProperty.Register(
        nameof(SinglePanelRatio), typeof(double), typeof(WorkspaceLayout),
        new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SplitRatioProperty = DependencyProperty.Register(
        nameof(SplitRatio), typeof(double), typeof(WorkspaceLayout),
        new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CellSizeProperty = DependencyProperty.Register(
        nameof(CellSize), typeof(Size), typeof(WorkspaceLayout),
        new FrameworkPropertyMetadata(new Size(8, 16), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static WorkspaceRole GetRole(UIElement element) => (WorkspaceRole)element.GetValue(RoleProperty);

    public static void SetRole(UIElement element, WorkspaceRole value) => element.SetValue(RoleProperty, value);

    public bool LeftVisible
    {
        get => (bool)GetValue(LeftVisibleProperty);
        set => SetValue(LeftVisibleProperty, value);
    }

    public bool RightVisible
    {
        get => (bool)GetValue(RightVisibleProperty);
        set => SetValue(RightVisibleProperty, value);
    }

    public int TerminalRows
    {
        get => (int)GetValue(TerminalRowsProperty);
        set => SetValue(TerminalRowsProperty, value);
    }

    public double SinglePanelRatio
    {
        get => (double)GetValue(SinglePanelRatioProperty);
        set => SetValue(SinglePanelRatioProperty, value);
    }

    /// <summary>Width share of the left panel when both panels are visible (Ctrl+Left / Ctrl+Right, splitter).</summary>
    public double SplitRatio
    {
        get => (double)GetValue(SplitRatioProperty);
        set => SetValue(SplitRatioProperty, value);
    }

    /// <summary>Terminal character cell size in device-independent pixels.</summary>
    public Size CellSize
    {
        get => (Size)GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

        // Each child is measured in its own slot. Measured with the whole workspace, a panel whose status line holds a
        // long file name wanted more than its slot, WPF arranged it at that width and clipped it, and the list columns
        // jumped as the cursor moved.
        var (left, right, terminal) = ComputeRects(size);
        foreach (UIElement child in InternalChildren)
        {
            var rect = GetRole(child) switch
            {
                WorkspaceRole.LeftPanel => left,
                WorkspaceRole.RightPanel => right,
                _ => terminal,
            };
            child.Measure(rect.IsEmpty ? new Size(0, 0) : rect.Size);
        }

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (left, right, terminal) = ComputeRects(finalSize);

        foreach (UIElement child in InternalChildren)
        {
            var rect = GetRole(child) switch
            {
                WorkspaceRole.LeftPanel => left,
                WorkspaceRole.RightPanel => right,
                _ => terminal,
            };

            child.Visibility = rect.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
            child.Arrange(rect.IsEmpty ? new Rect(0, 0, 0, 0) : rect);
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

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        var point = e.GetPosition(this);
        if (_dragging != Splitter.None)
        {
            Drag(point);
            e.Handled = true;
            return;
        }

        Cursor = SplitterAt(point) switch
        {
            Splitter.TerminalTop => Cursors.SizeNS,
            Splitter.Panels or Splitter.TerminalSide => Cursors.SizeWE,
            _ => null,
        };
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        var splitter = SplitterAt(e.GetPosition(this));
        if (splitter != Splitter.None)
        {
            _dragging = splitter;
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_dragging != Splitter.None)
        {
            _dragging = Splitter.None;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private Splitter SplitterAt(Point point)
    {
        var (left, right, terminal) = ComputeRects(RenderSize);
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
        var size = RenderSize;
        switch (_dragging)
        {
            case Splitter.Panels:
                SplitRatio = Math.Clamp(point.X / size.Width, 0.1, 0.9);
                break;
            case Splitter.TerminalTop:
                TerminalRows = Math.Max(MinTerminalRows, (int)Math.Round((size.Height - point.Y) / CellSize.Height));
                break;
            case Splitter.TerminalSide:
                var ratio = LeftVisible ? point.X / size.Width : 1 - (point.X / size.Width);
                SinglePanelRatio = Math.Clamp(ratio, 0.1, 0.9);
                break;
        }
    }

    private (Rect Left, Rect Right, Rect Terminal) ComputeRects(Size size)
    {
        var cell = CellSize;
        var full = new Rect(size);

        if (!LeftVisible && !RightVisible)
        {
            return (Rect.Empty, Rect.Empty, full);
        }

        if (LeftVisible && RightVisible)
        {
            var maxRows = Math.Max(MinTerminalRows, (int)Math.Floor((size.Height - (MinPanelRows * cell.Height)) / cell.Height));
            var rows = Math.Clamp(TerminalRows, MinTerminalRows, maxRows);
            var terminalHeight = Math.Min(size.Height, rows * cell.Height);
            var panelsHeight = size.Height - terminalHeight;
            var half = Math.Floor(size.Width * Math.Clamp(SplitRatio, 0.1, 0.9));
            return (
                new Rect(0, 0, half, panelsHeight),
                new Rect(half, 0, size.Width - half, panelsHeight),
                new Rect(0, panelsHeight, size.Width, terminalHeight));
        }

        // One panel: the terminal takes the place of the hidden one, snapped to whole columns.
        var panelWidth = Math.Floor(size.Width * Math.Clamp(SinglePanelRatio, 0.1, 0.9));
        var columns = Math.Max(1, Math.Floor((size.Width - panelWidth) / cell.Width));
        var terminalWidth = Math.Min(size.Width, columns * cell.Width);
        panelWidth = size.Width - terminalWidth;

        return LeftVisible
            ? (new Rect(0, 0, panelWidth, size.Height), Rect.Empty, new Rect(panelWidth, 0, terminalWidth, size.Height))
            : (Rect.Empty, new Rect(terminalWidth, 0, panelWidth, size.Height), new Rect(0, 0, terminalWidth, size.Height));
    }
}
