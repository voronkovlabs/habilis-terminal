using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
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
/// F9 (SPEC §5): VS Code-styled drop-downs for the menu headers in the title bar — Left / Files / Commands /
/// Options / Right. The popup draws its own copy of the headers exactly over the title bar's ones. Left / Right switch menus, Up / Down move, Enter runs, a letter jumps to the next command starting
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
    private readonly Control _target;
    private int _group;
    private int _item;

    private TopMenu(Control target, IReadOnlyList<MenuGroup> groups, int group)
    {
        _groups = groups;
        _group = group;
        _target = target;

        var barBorder = new Border { Child = _bar, Width = target.ActualWidth, Height = target.ActualHeight, HorizontalAlignment = HorizontalAlignment.Left };
        barBorder.SetResourceReference(Border.BackgroundProperty, "Habi.TitleBarBackground");
        for (var i = 0; i < groups.Count; i++)
        {
            var index = i;
            var header = CreateHeader(groups[i].Title, target.ActualHeight);
            header.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                SelectGroup(index);
            };
            header.PointerEntered += (_, _) => SelectGroup(index);
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
            BoxShadow = BoxShadows.Parse("0 4 16 0 #73000000"),
        };
        _dropDown.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        _dropDown.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");

        var layout = new StackPanel();
        layout.Children.Add(barBorder);
        layout.Children.Add(_dropDown);
        _root = new Border { Child = layout, Focusable = true,  Background = Brushes.Transparent };
        _root.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _root.AddHandler(Avalonia.Input.InputElement.TextInputEvent, OnTextInput, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Windows 11 draws the window's 1 px border over the top row of the title bar; the popup is a window of its own
        // and covered it, so the selected header stuck out above the frame. Its top row is left out.
        var edge = 1 / (TopLevel.GetTopLevel(target)?.RenderScaling ?? 1);
        if ((TopLevel.GetTopLevel(target) as Window) is { WindowState: not WindowState.Maximized } window && (target.TranslatePoint(default, window)?.Y ?? 0) < edge)
        {
            _root.Clip = new RectangleGeometry(new Rect(0, edge, 100_000, 100_000));
        }

        _popup = new Popup
        {
            ShouldUseOverlayLayer = true,
            Child = _root,
            PlacementTarget = target,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft,
            PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight,
            IsLightDismissEnabled = true,
            Focusable = true,
        };
        UiZoom.Apply(_popup);
        _popup.Closed += (_, _) => _result.TrySetResult(null);
        _popup.Opened += (_, _) => _root.Focus();
    }

    /// <summary>A menu header; the title bar and the popup build theirs alike so they line up.</summary>
    public static Border CreateHeader(string title, double height)
    {
        var text = Text(title);
        text.SetResourceReference(TextBlock.ForegroundProperty, "Habi.TitleBarForeground");
        return new Border { Padding = new Thickness(8, 0, 8, 0), Height = height, Child = text, Background = Brushes.Transparent, CornerRadius = new CornerRadius(3) };
    }

    public static async Task<MenuCommand?> ShowAsync(Control target, IReadOnlyList<MenuGroup> groups, int initialGroup)
    {
        var menu = new TopMenu(target, groups, Math.Clamp(initialGroup, 0, groups.Count - 1));
        menu.SelectGroup(menu._group);
        PopupFocus.Open(menu._popup);
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
                header.SetResourceReference(Border.BackgroundProperty, "Habi.TitleBarHover");
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

        // Under its header. The title bar's headers are laid out already; this popup's copy is not before it opens.
        var headers = _target is Panel strip && strip.Children.Count == _groups.Count ? strip.Children : _bar.Children;
        var left = 0.0;
        for (var i = 0; i < _group; i++)
        {
            left += ((Control)headers[i]).ActualWidth;
        }

        // Shifted left when it would cross the window's right edge (Right's drop-down in a narrow window).
        if ((TopLevel.GetTopLevel(_target) as Window) is { } window && _target.IsVisible)
        {
            _dropDown.Margin = default; // DesiredSize includes the margin of the previous menu
            _dropDown.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var room = window.ActualWidth - (_target.TranslatePoint(default, window)?.X ?? 0) - 8;
            left = Math.Max(0, Math.Min(left, room - _dropDown.DesiredSize.Width));
        }

        _dropDown.Margin = new Thickness(left, 0, 0, 0);
        _item = -1;
        Move(1);
    }

    private Control BuildRow(MenuCommand command, int index)
    {
        if (command.IsSeparator)
        {
            var line = new Rectangle { Height = 1, Margin = new Thickness(0, 4, 0, 4) };
            line.SetResourceReference(Shape.FillProperty, "Habi.WidgetBorder");
            return line;
        }

        var check = new Avalonia.Controls.Shapes.Path { Width = 12, Height = 12, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), Data = command.IsChecked ? Codicon.GeometryOf(Codicon.Check) : null };
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
        border.PointerEntered += (_, _) => Highlight(index);
        border.PointerReleased += (_, e) =>
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

            var label = (TextBlock)((DockPanel)border.Child!).Children[2];
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

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var key = e.Key;
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
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: 1 })
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
