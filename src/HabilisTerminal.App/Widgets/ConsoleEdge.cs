using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace HabilisTerminal.App.Widgets;

/// <summary>
/// The top edge of the console grown over the panels (issue #288): a line in the theme's accent, a soft shadow cast up
/// onto the panels, as the console lies on top of them, and on the right a small tab "Panels below · Esc" that says the
/// panels stay in place underneath. A click on the tab brings them back, like Esc. Arranged by
/// <see cref="Layout.WorkspaceLayout"/> just above the console while it is grown.
/// </summary>
internal sealed class ConsoleEdge : Grid
{
    /// <summary>The band above the console's top that the edge takes: the shadow, with the tab and the line at its bottom.</summary>
    public const double BandHeight = 22;

    private const double LineThickness = 2;

    public ConsoleEdge(Action collapse)
    {
        IsHitTestVisible = true;
        ZIndex = 10;

        Children.Add(new Border
        {
            IsHitTestVisible = false,
            Margin = new Thickness(0, 0, 0, LineThickness),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Color.FromArgb(0x66, 0, 0, 0), 1) },
            },
        });

        var line = new Border { IsHitTestVisible = false, Height = LineThickness, VerticalAlignment = VerticalAlignment.Bottom };
        line.SetResourceReference(Border.BackgroundProperty, "Habi.Accent");
        Children.Add(line);

        var text = new TextBlock { Text = "▾ Panels below · Esc", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Foreground");
        var tab = new Border
        {
            Child = text,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 28, 0),
            Padding = new Thickness(8, 1, 8, 2),
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            BorderThickness = new Thickness(1, 1, 1, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
        };
        tab.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        tab.SetResourceReference(Border.BorderBrushProperty, "Habi.Accent");
        ToolTip.SetTip(tab, "The console lies over the panels; they are still there underneath. Esc or a click brings them back.");
        tab.PointerPressed += (_, e) => e.Handled = true;
        tab.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            collapse();
        };
        Children.Add(tab);
    }
}
