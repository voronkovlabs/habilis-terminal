using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HabilisTerminal.App.Hosting;
using HabilisTerminal.App.Theming;
using HabilisTerminal.Core.Panels;

namespace HabilisTerminal.App.Widgets;

/// <summary>
/// The tabs below the console (issue #119), shown only while there are two or more. Each has a colour of its own
/// (issue #120): the current one is filled with it, the others carry it as a line on top. Tabs are wide enough for a
/// title and a detail; with many of them they narrow, and then the strip scrolls (issue #121). Nothing here takes the
/// keyboard focus: the Win32 focus stays in the console.
/// </summary>
internal sealed class ConsoleTabStrip : Border
{
    public const double StripHeight = 28;

    private const double ItemGap = 2;
    private const double ReservedForAdd = 40;
    private const double ReservedForArrows = 56;
    private const double ArrowStep = 120;
    private const double NoCloseBelow = 90;

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
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _arrows = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly List<TabView> _views = [];
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public ConsoleTabStrip()
    {
        Focusable = false;
        BorderThickness = new Thickness(0, 1, 0, 0);
        SetResourceReference(BackgroundProperty, "Habi.Background");
        SetResourceReference(BorderBrushProperty, "Habi.Border");

        _scroll = new ScrollViewer
        {
            Content = _row,
            Focusable = false,
            CanContentScroll = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var add = SmallButton("+", 16, "New console (Ctrl+Shift+T)", () => NewTabRequested?.Invoke());
        _arrows.Children.Add(SmallButton("‹", 15, "Scroll the tabs (or the mouse wheel)", () => ScrollBy(-ArrowStep)));
        _arrows.Children.Add(SmallButton("›", 15, "Scroll the tabs (or the mouse wheel)", () => ScrollBy(ArrowStep)));

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_scroll, 0);
        Grid.SetColumn(add, 1);
        Grid.SetColumn(_arrows, 3);
        layout.Children.Add(_scroll);
        layout.Children.Add(add);
        layout.Children.Add(_arrows);
        Child = layout;

        SizeChanged += (_, _) => ApplyWidths();
        PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            ScrollBy(e.Delta < 0 ? ArrowStep : -ArrowStep);
        };
        _clock.Tick += (_, _) => RefreshTexts();
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
        _views.Clear();
        Border? front = null;
        foreach (var tab in tabs)
        {
            var view = TabItem(tab, tab == current);
            _views.Add(view);
            _row.Children.Add(view.Item);
            if (tab == current)
            {
                front = view.Item;
            }
        }

        ApplyWidths();
        RefreshTexts();

        // Only the clock's text changes while a command runs: rebuilding would close tooltips and lose the hover.
        if (tabs.Any(t => t.CommandRunning))
        {
            _clock.Start();
        }
        else
        {
            _clock.Stop();
        }

        if (front is { } item)
        {
            Dispatcher.BeginInvoke(item.BringIntoView, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Stops the clock when the strip is not in use.</summary>
    public void Clear()
    {
        _clock.Stop();
        _row.Children.Clear();
        _views.Clear();
    }

    private void RefreshTexts()
    {
        foreach (var view in _views)
        {
            view.Title.Text = view.Tab.Title;
            var detail = view.Tab.Detail;
            view.Detail.Text = detail.Length > 0 ? "  " + detail : string.Empty;
            view.Tip.Content = view.Tab.Description; // in place: a new tooltip each second would close the open one
        }
    }

    private void ApplyWidths()
    {
        if (ActualWidth <= 0 || _views.Count == 0)
        {
            return;
        }

        var available = Math.Max(0, ActualWidth - ReservedForAdd);
        var scrolls = ConsoleTabRules.Scrolls(available, _views.Count);
        if (scrolls)
        {
            available = Math.Max(0, available - ReservedForArrows);
        }

        _arrows.Visibility = scrolls ? Visibility.Visible : Visibility.Collapsed;
        _scroll.MaxWidth = available;
        var (min, max) = ConsoleTabRules.TabWidths(available, _views.Count);
        foreach (var view in _views)
        {
            view.Item.MinWidth = Math.Max(0, min - ItemGap);
            view.Item.MaxWidth = Math.Max(0, max - ItemGap);
            view.Narrow = max < NoCloseBelow;
            view.ShowClose(hovered: false);
        }
    }

    private void ScrollBy(double delta) => _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset + delta);

    private TabView TabItem(ConsoleTab tab, bool current)
    {
        var color = ColorOf(tab.ColorIndex);
        Brush? text = current ? PanelPalette.ReadableOn(color) : null;

        var titleRun = new Run { FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal };
        var detailRun = new Run();
        if (text is not null)
        {
            titleRun.Foreground = text;
            var dim = text.Clone();
            dim.Opacity = 0.75;
            dim.Freeze();
            detailRun.Foreground = dim;
        }
        else
        {
            titleRun.SetResourceReference(TextElement.ForegroundProperty, "Habi.Foreground");
            detailRun.SetResourceReference(TextElement.ForegroundProperty, "Habi.Description");
        }

        var label = Label(string.Empty, 12);
        label.Inlines.Add(titleRun);
        label.Inlines.Add(detailRun);
        label.TextTrimming = TextTrimming.CharacterEllipsis;

        var close = Label("×", 14);
        if (text is not null)
        {
            close.Foreground = text;
        }
        else
        {
            close.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Foreground");
        }

        var closeButton = new Border { Child = close, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent, Focusable = false, ToolTip = Tip("Close console (Ctrl+Shift+W)") };
        OnClick(closeButton, () => TabClosing?.Invoke(tab));

        var content = new DockPanel { Margin = new Thickness(9, 0, 3, 0), LastChildFill = true };
        DockPanel.SetDock(closeButton, Dock.Right);
        content.Children.Add(closeButton);
        if (Mark(tab.Mark, text) is { } mark)
        {
            DockPanel.SetDock(mark, Dock.Left);
            content.Children.Add(mark);
        }

        content.Children.Add(label);

        // The console is above the strip: tabs hang from it, rounded at the bottom.
        var item = new Border
        {
            Child = content,
            Focusable = false,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, ItemGap, 3),
            CornerRadius = new CornerRadius(0, 0, 5, 5),
            BorderThickness = new Thickness(0, current ? 0 : 2, 0, 0),
            BorderBrush = color,
            Background = current ? color : Brushes.Transparent,
        };
        var tip = Tip(string.Empty);
        item.ToolTip = tip;
        var view = new TabView(tab, item, tip, titleRun, detailRun, closeButton, current);
        item.MouseEnter += (_, _) =>
        {
            view.ShowClose(hovered: true);
            if (!current)
            {
                item.SetResourceReference(BackgroundProperty, "Habi.Hover");
            }
        };
        item.MouseLeave += (_, _) =>
        {
            view.ShowClose(hovered: false);
            if (!current)
            {
                item.Background = Brushes.Transparent;
            }
        };
        OnClick(item, () => TabChosen?.Invoke(tab));
        return view;
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

    private static Border SmallButton(string text, double size, string tip, Action click)
    {
        var label = Label(text, size);
        label.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Description");
        label.Margin = new Thickness(9, -2, 9, 0);
        var button = new Border { Child = label, Background = Brushes.Transparent, Focusable = false, Cursor = Cursors.Hand, CornerRadius = new CornerRadius(4), Margin = new Thickness(2, 3, 0, 3), ToolTip = Tip(tip) };
        button.MouseEnter += (_, _) => button.SetResourceReference(BackgroundProperty, "Habi.Hover");
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        OnClick(button, click);
        return button;
    }

    /// <summary>A tooltip in the theme's colours; the default one is white.</summary>
    internal static ToolTip Tip(string text)
    {
        var tip = new ToolTip { Content = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 12, Padding = new Thickness(8, 5, 8, 5) };
        tip.SetResourceReference(Control.ForegroundProperty, "Habi.Foreground");
        tip.SetResourceReference(Control.BackgroundProperty, "Habi.WidgetBackground");
        tip.SetResourceReference(Control.BorderBrushProperty, "Habi.WidgetBorder");
        return tip;
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

    private sealed class TabView(ConsoleTab tab, Border item, ToolTip tip, Run title, Run detail, Border close, bool current)
    {
        public ConsoleTab Tab { get; } = tab;

        public Border Item { get; } = item;

        public ToolTip Tip { get; } = tip;

        public Run Title { get; } = title;

        public Run Detail { get; } = detail;

        /// <summary>Too little room to keep the × there: it comes on hover, and takes no space until then.</summary>
        public bool Narrow { get; set; }

        public void ShowClose(bool hovered)
        {
            close.Visibility = Narrow && !hovered ? Visibility.Collapsed : Visibility.Visible;
            close.Opacity = hovered ? 1 : current ? 0.8 : 0;
        }
    }
}
