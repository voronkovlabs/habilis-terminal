using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using HabilisTerminal.App.Hosting;

namespace HabilisTerminal.App.Widgets;

/// <summary>
/// The tabs above the console (issue #119), shown only while there are two or more. Nothing here takes the keyboard
/// focus: the Win32 focus stays in the console.
/// </summary>
internal sealed class ConsoleTabStrip : Border
{
    public const double StripHeight = 26;

    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };

    public ConsoleTabStrip()
    {
        Focusable = false;
        Child = _row;
        BorderThickness = new Thickness(0, 1, 0, 1);
        SetResourceReference(BackgroundProperty, "Habi.Background");
        SetResourceReference(BorderBrushProperty, "Habi.Border");
    }

    public event Action<ConsoleTab>? TabChosen;

    public event Action<ConsoleTab>? TabClosing;

    public event Action? NewTabRequested;

    public void Update(IReadOnlyList<ConsoleTab> tabs, ConsoleTab? current)
    {
        _row.Children.Clear();
        foreach (var tab in tabs)
        {
            _row.Children.Add(TabItem(tab, tab == current));
        }

        var add = Label("+", "Habi.Description", 14);
        add.Margin = new Thickness(8, 0, 8, 0);
        var addButton = Clickable(add, background: null, () => NewTabRequested?.Invoke());
        addButton.ToolTip = "New console (Ctrl+Shift+T)";
        _row.Children.Add(addButton);
    }

    private Border TabItem(ConsoleTab tab, bool current)
    {
        var foreground = current ? "Habi.SelectionForeground" : "Habi.Foreground";
        var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 4, 0) };
        if (Mark(tab.Mark, foreground) is { } mark)
        {
            content.Children.Add(mark);
        }

        var title = Label(tab.Title, foreground, 12);
        title.MaxWidth = 220;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        content.Children.Add(title);

        var close = Label("×", foreground, 14);
        close.Margin = new Thickness(4, 0, 4, 0);
        var closeButton = Clickable(close, background: null, () => TabClosing?.Invoke(tab));
        closeButton.ToolTip = "Close console (Ctrl+Shift+W)";
        content.Children.Add(closeButton);

        // In some themes the selection has the strip's own colour; the line marks the current tab in all of them.
        var underline = new Border { Child = content, BorderThickness = new Thickness(0, 0, 0, 2), Focusable = false };
        if (current)
        {
            underline.SetResourceReference(BorderBrushProperty, "Habi.Accent");
        }

        var item = Clickable(underline, current ? "Habi.Selection" : null, () => TabChosen?.Invoke(tab));
        item.BorderThickness = new Thickness(0, 0, 1, 0);
        item.SetResourceReference(BorderBrushProperty, "Habi.Border");
        item.ToolTip = tab.Title;
        return item;
    }

    private static TextBlock? Mark(ConsoleTabMark mark, string foreground)
    {
        if (mark == ConsoleTabMark.None)
        {
            return null;
        }

        var text = mark switch
        {
            ConsoleTabMark.Running => "●",
            ConsoleTabMark.Succeeded => "✓",
            _ => "✕",
        };
        var brush = mark switch
        {
            ConsoleTabMark.Running => foreground,
            ConsoleTabMark.Succeeded => "Habi.ToneGood",
            _ => "Habi.Error",
        };
        var block = Label(text, brush, 11);
        block.FontFamily = new FontFamily("Segoe UI Symbol");
        block.Margin = new Thickness(0, 0, 6, 0);
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

    private static TextBlock Label(string text, string foreground, double size)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        return block;
    }

    private static Border Clickable(UIElement content, string? background, Action click)
    {
        var border = new Border { Child = content, Focusable = false, Cursor = Cursors.Hand };
        void Paint(string? key)
        {
            if (key is null)
            {
                border.Background = Brushes.Transparent; // so that the whole area takes the mouse
            }
            else
            {
                border.SetResourceReference(BackgroundProperty, key);
            }
        }

        Paint(background);
        border.MouseEnter += (_, _) => Paint(background ?? "Habi.Hover");
        border.MouseLeave += (_, _) => Paint(background);
        border.MouseLeftButtonDown += (_, e) => e.Handled = true;
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return border;
    }
}
