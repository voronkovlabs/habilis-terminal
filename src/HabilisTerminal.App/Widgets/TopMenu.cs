using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using HabilisTerminal.App.Theming;

namespace HabilisTerminal.App.Widgets;

/// <summary>A command in the F9 menu; <see cref="Separator"/> draws a line.</summary>
public sealed record MenuCommand(string Label, string? Shortcut, Action? Run, bool IsChecked = false)
{
    public static MenuCommand Separator { get; } = new("-", null, null);

    public bool IsSeparator => Run is null;
}

public sealed record MenuGroup(string Title, IReadOnlyList<MenuCommand> Commands);

/// <summary>
/// F9 (SPEC §5): a menu bar across the top of the window with VS Code-styled drop-downs — Left / Files / Commands /
/// Options / Right. Left / Right switch menus, Up / Down move, Enter runs, a letter jumps to the next command starting
/// with it, Esc / F9 / F10 close. The chosen command runs after the menu has closed.
/// </summary>
public sealed class TopMenu
{
    private readonly IReadOnlyList<MenuGroup> _groups;
    private readonly Popup _popup;
    private readonly StackPanel _bar = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _items = new();
    private readonly Border _dropDown;
    private readonly TaskCompletionSource<MenuCommand?> _result = new();
    private readonly Border _root;
    private int _group;
    private int _item;

    private TopMenu(FrameworkElement target, IReadOnlyList<MenuGroup> groups, int group)
    {
        _groups = groups;
        _group = group;

        var barBorder = new Border { Child = _bar, BorderThickness = new Thickness(0, 0, 0, 1), Width = target.ActualWidth, Padding = new Thickness(4, 0, 4, 0) };
        barBorder.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        barBorder.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");
        for (var i = 0; i < groups.Count; i++)
        {
            var index = i;
            var header = new Border { Padding = new Thickness(10, 5, 10, 5), Child = Text(groups[i].Title), Background = Brushes.Transparent };
            header.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                SelectGroup(index);
            };
            header.MouseEnter += (_, _) => SelectGroup(index);
            _bar.Children.Add(header);
        }

        _dropDown = new Border
        {
            Child = _items,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0, 0, 4, 4),
            Padding = new Thickness(0, 4, 0, 4),
            MinWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Left,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.4, Color = Colors.Black },
        };
        _dropDown.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        _dropDown.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");

        var layout = new StackPanel();
        layout.Children.Add(barBorder);
        layout.Children.Add(_dropDown);
        _root = new Border { Child = layout, Focusable = true, FocusVisualStyle = null, Background = Brushes.Transparent };
        _root.PreviewKeyDown += OnKeyDown;
        _root.PreviewTextInput += OnTextInput;

        _popup = new Popup
        {
            Child = _root,
            PlacementTarget = target,
            Placement = PlacementMode.Relative,
            AllowsTransparency = true,
            StaysOpen = false,
            Focusable = true,
        };
        _popup.Closed += (_, _) => _result.TrySetResult(null);
        _popup.Opened += (_, _) => Keyboard.Focus(_root);
    }

    public static async Task<MenuCommand?> ShowAsync(FrameworkElement target, IReadOnlyList<MenuGroup> groups, int initialGroup)
    {
        var menu = new TopMenu(target, groups, Math.Clamp(initialGroup, 0, groups.Count - 1));
        menu.SelectGroup(menu._group);
        menu._popup.IsOpen = true;
        return await menu._result.Task;
    }

    private void SelectGroup(int index)
    {
        _group = (index + _groups.Count) % _groups.Count;
        for (var i = 0; i < _bar.Children.Count; i++)
        {
            var header = (Border)_bar.Children[i];
            if (i == _group)
            {
                header.SetResourceReference(Border.BackgroundProperty, "Habi.Hover");
            }
            else
            {
                header.Background = Brushes.Transparent;
            }
        }

        _items.Children.Clear();
        var commands = _groups[_group].Commands;
        for (var i = 0; i < commands.Count; i++)
        {
            _items.Children.Add(BuildRow(commands[i], i));
        }

        // Under its header.
        _bar.UpdateLayout();
        var left = 4.0;
        for (var i = 0; i < _group; i++)
        {
            left += ((FrameworkElement)_bar.Children[i]).ActualWidth;
        }

        _dropDown.Margin = new Thickness(left, 0, 0, 0);
        _item = -1;
        Move(1);
    }

    private FrameworkElement BuildRow(MenuCommand command, int index)
    {
        if (command.IsSeparator)
        {
            var line = new Rectangle { Height = 1, Margin = new Thickness(0, 4, 0, 4) };
            line.SetResourceReference(Shape.FillProperty, "Habi.WidgetBorder");
            return line;
        }

        var check = new System.Windows.Shapes.Path { Width = 12, Height = 12, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), Data = command.IsChecked ? Codicon.GeometryOf(Codicon.Check) : null };
        check.SetResourceReference(Shape.FillProperty, "Habi.Foreground");
        var shortcut = Text(command.Shortcut ?? string.Empty);
        shortcut.Margin = new Thickness(32, 0, 0, 0);
        shortcut.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Description");
        var row = new DockPanel();
        DockPanel.SetDock(check, Dock.Left);
        DockPanel.SetDock(shortcut, Dock.Right);
        row.Children.Add(check);
        row.Children.Add(shortcut);
        row.Children.Add(Text(command.Label));
        var border = new Border { Child = row, Padding = new Thickness(10, 4, 14, 4), Margin = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(3), Background = Brushes.Transparent };
        border.MouseEnter += (_, _) => Highlight(index);
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Accept(index);
        };
        return border;
    }

    private void Highlight(int index)
    {
        _item = index;
        for (var i = 0; i < _items.Children.Count; i++)
        {
            if (_items.Children[i] is not Border border)
            {
                continue;
            }

            var label = (TextBlock)((DockPanel)border.Child).Children[2];
            if (i == index)
            {
                border.SetResourceReference(Border.BackgroundProperty, "Habi.Selection");
                label.SetResourceReference(TextBlock.ForegroundProperty, "Habi.SelectionForeground");
            }
            else
            {
                border.Background = Brushes.Transparent;
                label.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Foreground");
            }
        }
    }

    private void Move(int direction)
    {
        var commands = _groups[_group].Commands;
        if (commands.All(c => c.IsSeparator))
        {
            return;
        }

        var index = _item;
        do
        {
            index = (index + direction + commands.Count) % commands.Count;
        }
        while (commands[index].IsSeparator);
        Highlight(index);
    }

    private void Accept(int index)
    {
        var commands = _groups[_group].Commands;
        if (index < 0 || index >= commands.Count || commands[index].IsSeparator)
        {
            return;
        }

        _result.TrySetResult(commands[index]);
        _popup.IsOpen = false;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        switch (key)
        {
            case Key.Left:
                SelectGroup(_group - 1);
                break;
            case Key.Right:
                SelectGroup(_group + 1);
                break;
            case Key.Up:
                Move(-1);
                break;
            case Key.Down:
                Move(1);
                break;
            case Key.Home:
                _item = -1;
                Move(1);
                break;
            case Key.End:
                _item = 0;
                Move(-1);
                break;
            case Key.Enter or Key.Space:
                Accept(_item);
                break;
            case Key.Escape or Key.F9 or Key.F10:
                _popup.IsOpen = false;
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    /// <summary>A letter moves to the next command that starts with it.</summary>
    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length != 1)
        {
            return;
        }

        e.Handled = true;
        var commands = _groups[_group].Commands;
        for (var step = 1; step <= commands.Count; step++)
        {
            var index = (_item + step) % commands.Count;
            if (!commands[index].IsSeparator && commands[index].Label.StartsWith(e.Text, StringComparison.CurrentCultureIgnoreCase))
            {
                Highlight(index);
                return;
            }
        }
    }

    private static TextBlock Text(string text)
    {
        var block = new TextBlock { Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Foreground");
        return block;
    }
}
