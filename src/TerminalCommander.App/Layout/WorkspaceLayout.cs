using System.Windows;
using System.Windows.Controls;

namespace TerminalCommander.App.Layout;

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
        new FrameworkPropertyMetadata(WorkspaceRole.Terminal, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty LeftVisibleProperty = DependencyProperty.Register(
        nameof(LeftVisible), typeof(bool), typeof(WorkspaceLayout),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty RightVisibleProperty = DependencyProperty.Register(
        nameof(RightVisible), typeof(bool), typeof(WorkspaceLayout),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty TerminalRowsProperty = DependencyProperty.Register(
        nameof(TerminalRows), typeof(int), typeof(WorkspaceLayout),
        new FrameworkPropertyMetadata(10, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty SinglePanelRatioProperty = DependencyProperty.Register(
        nameof(SinglePanelRatio), typeof(double), typeof(WorkspaceLayout),
        new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty CellSizeProperty = DependencyProperty.Register(
        nameof(CellSize), typeof(Size), typeof(WorkspaceLayout),
        new FrameworkPropertyMetadata(new Size(8, 16), FrameworkPropertyMetadataOptions.AffectsArrange));

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

    /// <summary>Terminal character cell size in device-independent pixels.</summary>
    public Size CellSize
    {
        get => (Size)GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(availableSize);
        }

        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
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
            var half = Math.Floor(size.Width / 2);
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
