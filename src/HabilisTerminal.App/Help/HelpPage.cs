using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;

namespace HabilisTerminal.App.Help;

/// <summary>F1 (SPEC §5): the keyboard map as a full-window page. Esc, F1 or F10 closes it.</summary>
public sealed class HelpPage : DockPanel, IPage
{
    private readonly ScrollViewer _scroll;

    public HelpPage()
    {
        LastChildFill = true;
        this.SetResourceReference(BackgroundProperty, "Habi.Background");

        var title = Text("Keyboard", 12, FontWeight.SemiBold, "Habi.Foreground");
        var hint = Text("Esc close", 12, FontWeight.Normal, "Habi.Description");
        var header = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
        DockPanel.SetDock(hint, Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(title);
        var headerBorder = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        headerBorder.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");
        SetDock(headerBorder, Dock.Top);
        Children.Add(headerBorder);

        var columns = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 16, 24, 24) };
        foreach (var (section, rows) in Core.Commands.CommandCatalog.HelpSections)
        {
            var grid = new Grid { Width = 460, Margin = new Thickness(0, 0, 32, 24) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = Text(section, 14, FontWeight.SemiBold, "Habi.Foreground");
            heading.Margin = new Thickness(0, 0, 0, 6);
            Grid.SetColumnSpan(heading, 2);
            grid.Children.Add(heading);
            foreach (var (keys, action) in rows)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var keyText = Text(keys, 13, FontWeight.Normal, "Habi.Accent");
                keyText.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                keyText.FontSize = 12;
                var actionText = Text(action, 13, FontWeight.Normal, "Habi.Foreground");
                actionText.TextWrapping = TextWrapping.Wrap;
                foreach (var cell in new[] { keyText, actionText })
                {
                    cell.Margin = new Thickness(0, 2, 8, 2);
                    Grid.SetRow(cell, grid.RowDefinitions.Count - 1);
                    grid.Children.Add(cell);
                }

                Grid.SetColumn(actionText, 1);
            }

            columns.Children.Add(grid);
        }

        _scroll = new ScrollViewer { Content = columns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = true,  };
        Children.Add(_scroll);
        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public event Action? CloseRequested;

    public void FocusContent() => _scroll.Focus();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F1 or Key.F10 && Keyboard.Modifiers == KeyModifiers.None)
        {
            e.Handled = true;
            CloseRequested?.Invoke();
        }
    }

    private static TextBlock Text(string text, double size, FontWeight weight, string brushKey)
    {
        var block = new TextBlock { Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = size, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }
}
