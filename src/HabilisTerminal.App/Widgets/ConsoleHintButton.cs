using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace HabilisTerminal.App.Widgets;

/// <summary>
/// The half-transparent button in the console's bottom right corner (issue #120), a hint that a second console is
/// possible. The console is a child window, so only a popup can lie over it; like the operation cards it closes when
/// the window is not active, as popups stay above every other program.
/// </summary>
internal sealed class ConsoleHintButton
{
    private const double Size = 30;
    private const double CornerGap = 24; // the same from both edges, and clear of the console's scroll bar
    private const double RestOpacity = 0.5;

    private readonly Window _owner;
    private readonly Popup _popup;
    private bool _wanted;

    public ConsoleHintButton(Window owner, FrameworkElement target, Action click)
    {
        _owner = owner;
        var plus = new TextBlock
        {
            Text = "+",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -3, 0, 0),
        };
        plus.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Foreground");
        var button = new Border
        {
            Width = Size,
            Height = Size,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = plus,
            Cursor = Cursors.Hand,
            Opacity = RestOpacity,
            Focusable = false,
        };

        // Left of the button, in the theme's colours: the default one is white and opens past the window's edge.
        var tip = ConsoleTabStrip.Tip("New console (Ctrl+Shift+T)\nA long command goes on in its tab while you work in another one.");
        tip.Placement = PlacementMode.Left;
        button.ToolTip = tip;
        button.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        button.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");
        button.MouseEnter += (_, _) => button.Opacity = 1;
        button.MouseLeave += (_, _) => button.Opacity = RestOpacity;
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };

        _popup = new Popup
        {
            Child = button,
            PlacementTarget = target,
            Placement = PlacementMode.Relative,
            AllowsTransparency = true,
            StaysOpen = true,
            Focusable = false,
        };
        UiZoom.Apply(_popup);
        owner.Activated += (_, _) => Apply();
        owner.Deactivated += (_, _) => _popup.IsOpen = false;
        owner.StateChanged += (_, _) => Apply();
        owner.LocationChanged += (_, _) => Nudge();
    }

    /// <param name="console">The console's area in the target's coordinates; null hides the button.</param>
    public void Update(Rect? console)
    {
        _wanted = console is not null;
        if (console is { } area)
        {
            var x = Math.Max(0, area.Right - Size - CornerGap);
            var y = Math.Max(0, area.Bottom - Size - CornerGap);
            if (_popup.HorizontalOffset != x || _popup.VerticalOffset != y)
            {
                _popup.HorizontalOffset = x;
                _popup.VerticalOffset = y;
            }
        }

        Apply();
    }

    private void Apply() => _popup.IsOpen = _wanted && _owner.IsActive && _owner.WindowState != WindowState.Minimized;

    /// <summary>Popups only move when an offset actually changes.</summary>
    private void Nudge()
    {
        _popup.HorizontalOffset += 0.01;
        _popup.HorizontalOffset -= 0.01;
    }
}
