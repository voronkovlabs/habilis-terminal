using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using HabilisTerminal.App.Hosting;
using HabilisTerminal.App.Theming;

namespace HabilisTerminal.App.Widgets;

/// <summary>
/// The tabs below the console (issue #119), shown only while there are two or more. Each has a colour of its own
/// (issue #120): the current one is filled with it, the others carry it as a line on top. Nothing here takes the
/// keyboard focus: the Win32 focus stays in the console.
/// </summary>
internal sealed class ConsoleTabStrip : Border
{
    public const double StripHeight = 28;

    /// <summary>The colours after the first tab's, which is the theme's own focus colour.</summary>
    private static readonly Color[] TabColors =
    [
        Color.FromRgb(0x2E, 0xA0, 0x5F), // green
        Color.FromRgb(0xD9, 0x82, 0x2B), // orange
        Color.FromRgb(0x9B, 0x5C, 0xD6), // purple
        Color.FromRgb(0x17, 0xA2, 0xA8), // teal
        Color.FromRgb(0xD6, 0x4F, 0x97), // pink
        Color.FromRgb(0xB5, 0xA0, 0x1E), // olive
    ];

    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };

    public ConsoleTabStrip()
    {
        Focusable = false;
        Child = _row;
        BorderThickness = new Thickness(0, 1, 0, 0);
        SetResourceReference(BackgroundProperty, "Habi.Background");
        SetResourceReference(BorderBrushProperty, "Habi.Border");
    }

    public event Action<ConsoleTab>? TabChosen;

    public event Action<ConsoleTab>? TabClosing;

    public event Action? NewTabRequested;

    /// <summary>How many colours there are, the theme's included.</summary>
    public static int ColorCount => TabColors.Length + 1;

    /// <summary>A tab's colour; index 0 follows the theme, so one console looks as it always did.</summary>
    public static Brush ColorOf(int index)
    {
        if (index == 0)
        {
            return PanelPalette.FocusOutline;
        }

        var brush = new SolidColorBrush(TabColors[(index - 1) % TabColors.Length]);
        brush.Freeze();
        return brush;
    }

    public void Update(IReadOnlyList<ConsoleTab> tabs, ConsoleTab? current)
    {
        _row.Children.Clear();
        foreach (var tab in tabs)
        {
            _row.Children.Add(TabItem(tab, tab == current));
        }

        var add = Label("+", 16);
        add.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Description");
        add.Margin = new Thickness(9, -2, 9, 0);
        var addButton = new Border { Child = add, Background = Brushes.Transparent, Focusable = false, Cursor = Cursors.Hand, CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 3, 0, 3), ToolTip = "New console (Ctrl+Shift+T)" };
        addButton.MouseEnter += (_, _) => addButton.SetResourceReference(BackgroundProperty, "Habi.Hover");
        addButton.MouseLeave += (_, _) => addButton.Background = Brushes.Transparent;
        OnClick(addButton, () => NewTabRequested?.Invoke());
        _row.Children.Add(addButton);
    }

    private Border TabItem(ConsoleTab tab, bool current)
    {
        var color = ColorOf(tab.ColorIndex);
        Brush? text = current ? PanelPalette.ReadableOn(color) : null;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 4, 0) };
        if (Mark(tab.Mark, text) is { } mark)
        {
            content.Children.Add(mark);
        }

        var title = Label(tab.Title, 12);
        Paint(title, text);
        title.MaxWidth = 220;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        if (current)
        {
            title.FontWeight = FontWeights.SemiBold;
        }

        content.Children.Add(title);

        var close = Label("×", 14);
        Paint(close, text);
        var closeButton = new Border { Child = close, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent, Focusable = false, ToolTip = "Close console (Ctrl+Shift+W)", Opacity = current ? 0.8 : 0 };
        OnClick(closeButton, () => TabClosing?.Invoke(tab));
        content.Children.Add(closeButton);

        // The console is above the strip: tabs hang from it, rounded at the bottom.
        var item = new Border
        {
            Child = content,
            Focusable = false,
            Cursor = Cursors.Hand,
            ToolTip = tab.Title,
            Margin = new Thickness(0, 0, 2, 3),
            CornerRadius = new CornerRadius(0, 0, 5, 5),
            BorderThickness = new Thickness(0, current ? 0 : 2, 0, 0),
            BorderBrush = color,
            Background = current ? color : Brushes.Transparent,
        };
        item.MouseEnter += (_, _) =>
        {
            closeButton.Opacity = 1;
            if (!current)
            {
                item.SetResourceReference(BackgroundProperty, "Habi.Hover");
            }
        };
        item.MouseLeave += (_, _) =>
        {
            closeButton.Opacity = current ? 0.8 : 0;
            if (!current)
            {
                item.Background = Brushes.Transparent;
            }
        };
        OnClick(item, () => TabChosen?.Invoke(tab));
        return item;
    }

    /// <summary>On the current tab the mark takes the text's colour, which is readable on the tab's fill.</summary>
    private static TextBlock? Mark(ConsoleTabMark mark, Brush? text)
    {
        if (mark == ConsoleTabMark.None)
        {
            return null;
        }

        var block = Label(mark switch
        {
            ConsoleTabMark.Running => "●",
            ConsoleTabMark.Succeeded => "✓",
            _ => "✕",
        }, 11);
        block.FontFamily = new FontFamily("Segoe UI Symbol");
        block.Margin = new Thickness(0, 0, 6, 0);
        if (text is not null)
        {
            block.Foreground = text;
        }
        else
        {
            block.SetResourceReference(TextBlock.ForegroundProperty, mark switch
            {
                ConsoleTabMark.Running => "Habi.Foreground",
                ConsoleTabMark.Succeeded => "Habi.ToneGood",
                _ => "Habi.Error",
            });
        }

        if (mark == ConsoleTabMark.Running)
        {
            block.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }

        return block;
    }

    private static void Paint(TextBlock block, Brush? text)
    {
        if (text is not null)
        {
            block.Foreground = text;
        }
        else
        {
            block.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Foreground");
        }
    }

    private static TextBlock Label(string text, double size) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
        Focusable = false,
    };

    private static void OnClick(Border border, Action click)
    {
        border.MouseLeftButtonDown += (_, e) => e.Handled = true;
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
    }
}
