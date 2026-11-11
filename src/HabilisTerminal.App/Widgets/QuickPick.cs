using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using HabilisTerminal.App.Theming;

namespace HabilisTerminal.App.Widgets;

public sealed record QuickPickOptions
{
    public string Title { get; init; } = string.Empty;

    public string Placeholder { get; init; } = "Type to filter";

    public string InitialText { get; init; } = string.Empty;

    /// <summary>Enter accepts the typed text when no item matches (or when there are no items at all).</summary>
    public bool AllowFreeText { get; init; }

    /// <summary>
    /// Text that means itself, not a filter (a path in the drive menu, issue #142): the list shows nothing and Enter
    /// gives the text. It could otherwise match an item's description and open that.
    /// </summary>
    public Func<string, bool>? IsFreeText { get; init; }

    /// <summary>The list for what is typed, in place of filtering the items given (the command line, issue #155).</summary>
    public Func<string, IReadOnlyList<QuickPickItem>>? ItemsFor { get; init; }

    /// <summary>Opens over the target, as wide as it or wider, as if typing in the target itself.</summary>
    public bool OverTarget { get; init; }

    /// <summary>Del on an item (e.g. disconnect a drive); the item list is not changed automatically.</summary>
    public Action<QuickPickItem>? OnDelete { get; init; }

    /// <summary>Keys that close the pick with the selected item and the key as <see cref="QuickPickResult.Command"/> (F4 in the drive menu).</summary>
    public IReadOnlyList<Key> CommandKeys { get; init; } = [];
}

/// <summary>
/// Result: the chosen item, or free text when <see cref="QuickPickOptions.AllowFreeText"/> was used; with
/// <see cref="Command"/> one of <see cref="QuickPickOptions.CommandKeys"/> was pressed on the item.
/// </summary>
public sealed record QuickPickResult(QuickPickItem? Item, string Text, Key? Command = null);

/// <summary>A VS Code-like quick pick shown in a popup over its target (drive menu, history, sort menu, input).</summary>
public sealed class QuickPick : UserControl
{
    private readonly IReadOnlyList<QuickPickItem> _items;
    private readonly QuickPickOptions _options;
    private readonly TaskCompletionSource<QuickPickResult?> _result = new();
    private readonly TextBlock _title = new() { Margin = new Thickness(4, 0, 4, 6), FontSize = 12 };
    private readonly TextBox _filter = new() { Padding = new Thickness(6, 4), BorderThickness = new Thickness(1) };
    private readonly TextBlock _placeholder = new() { IsHitTestVisible = false, Margin = new Thickness(9, 5, 0, 0) };
    private readonly ListBox _list = new() { MaxHeight = 420, Background = Brushes.Transparent, BorderThickness = default };
    private Popup? _popup;

    private QuickPick(IReadOnlyList<QuickPickItem> items, QuickPickOptions options)
    {
        _items = items;
        _options = options;
        Width = 520;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        _title.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Description");
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Placeholder");
        _filter.SetResourceReference(TextBox.BorderBrushProperty, "Habi.Accent");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.ItemTemplate = new FuncDataTemplate<QuickPickItem>((item, _) => Row(item));
        _list.Styles.Add(PickItemStyle());

        var filterGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        filterGrid.Children.Add(_filter);
        filterGrid.Children.Add(_placeholder);
        var dock = new DockPanel();
        DockPanel.SetDock(_title, Dock.Top);
        DockPanel.SetDock(filterGrid, Dock.Top);
        dock.Children.Add(_title);
        dock.Children.Add(filterGrid);
        dock.Children.Add(_list);
        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            Margin = new Thickness(12, 4, 12, 16),
            BoxShadow = BoxShadows.Parse("0 4 16 0 #80000000"),
            Child = dock,
        };
        frame.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        frame.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");
        Content = frame;

        _title.Text = options.Title;
        _title.IsVisible = !string.IsNullOrEmpty(options.Title);
        _placeholder.Text = options.Placeholder;
        _list.IsVisible = !(items.Count == 0 && options.ItemsFor is null);

        _filter.Text = options.InitialText;
        _filter.TextChanged += (_, _) => ApplyFilter();
        _filter.AddHandler(KeyDownEvent, OnFilterKeyDown, RoutingStrategies.Tunnel);
        _filter.AddHandler(TextInputEvent, OnFilterTextInput, RoutingStrategies.Tunnel);
        _list.DoubleTapped += (_, _) => AcceptSelected();
        _list.Tapped += (_, e) =>
        {
            if (e.Source is Control { DataContext: QuickPickItem })
            {
                AcceptSelected();
            }
        };
        ApplyFilter();
    }

    public static Task<QuickPickResult?> ShowAsync(Control target, IReadOnlyList<QuickPickItem> items, QuickPickOptions options)
    {
        var pick = new QuickPick(items, options);
        const double Shadow = 12; // the frame's margin for its shadow
        var popup = new Popup
        {
            Child = pick,
            PlacementTarget = target,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.BottomRight,
            IsLightDismissEnabled = true,
            Focusable = true,
        };
        UiZoom.Apply(popup);

        // Centered horizontally near the top of the target, like the VS Code quick pick.
        var width = target.Bounds.Width;
        if (options.OverTarget)
        {
            const double Padding = 7; // the frame around the filter box, so that it lies over the target
            pick.Width = Math.Max(pick.Width, width + (2 * Padding) + (2 * Shadow));
            popup.HorizontalOffset = ((width - pick.Width) / 2) * UiZoom.Factor;
            popup.VerticalOffset = (-Padding - 4) * UiZoom.Factor;
        }
        else
        {
            popup.HorizontalOffset = Math.Max(0, (width - pick.Width) / 2) * UiZoom.Factor;
            popup.VerticalOffset = 4 * UiZoom.Factor;
        }

        // The popup lives in the window's tree: resources resolve and Escape reaches it.
        if (TopLevel.GetTopLevel(target) is { } top && OverlayHost(top) is { } host)
        {
            host.Children.Add(popup);
            popup.Closed += (_, _) => host.Children.Remove(popup);
        }

        popup.Closed += (_, _) => pick._result.TrySetResult(null);
        popup.Opened += (_, _) =>
        {
            pick._filter.Focus();

            // Like VS Code: an initial value is selected, so typing replaces it.
            pick._filter.SelectAll();
        };

        pick._popup = popup;
        PopupFocus.Open(popup);
        return pick._result.Task;
    }

    /// <summary>The panel popups are parked in while open: the window's root panel.</summary>
    internal static Panel? OverlayHost(TopLevel top) => top.Content switch
    {
        LayoutTransformControl { Child: Panel panel } => panel,
        Panel panel => panel,
        _ => null,
    };

    private static Style PickItemStyle()
    {
        var style = new Style(x => Selectors.OfType<ListBoxItem>(x));
        style.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(8, 3, 8, 3)));
        style.Setters.Add(new Setter(ListBoxItem.CornerRadiusProperty, new CornerRadius(3)));
        style.Setters.Add(new Setter(ListBoxItem.MinHeightProperty, 0d));
        return style;
    }

    private static StackPanel Row(QuickPickItem item)
    {
        var group = new TextBlock { Text = item.Group, IsVisible = item.HasGroup, FontSize = 11, Margin = new Thickness(0, 6, 0, 2) };
        group.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Description");
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = item.IconGeometry,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(Shape.FillProperty, "Habi.Description");
        var label = new TextBlock { Text = item.Label, VerticalAlignment = VerticalAlignment.Center };
        var description = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        description.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Description");
        var detail = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, FontSize = 12 };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Description");
        var usage = new ProgressBar { Height = 3, Margin = new Thickness(0, 2, 0, 0), Minimum = 0, Maximum = 1 };
        var right = new StackPanel { Margin = new Thickness(12, 0, 0, 0), MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(detail);
        right.Children.Add(usage);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("24,Auto,*,Auto") };
        Grid.SetColumn(label, 1);
        Grid.SetColumn(description, 2);
        Grid.SetColumn(right, 3);
        grid.Children.Add(icon);
        grid.Children.Add(label);
        grid.Children.Add(description);
        grid.Children.Add(right);
        var stack = new StackPanel();
        stack.Children.Add(group);
        stack.Children.Add(grid);

        void Update()
        {
            description.Text = item.Description;
            detail.Text = item.Detail;
            usage.IsVisible = item.HasUsage;
            usage.Value = item.Usage ?? 0;
        }

        void OnChanged(object? sender, PropertyChangedEventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Update);

        Update();
        stack.AttachedToVisualTree += (_, _) => item.PropertyChanged += OnChanged;
        stack.DetachedFromVisualTree += (_, _) => item.PropertyChanged -= OnChanged;
        return stack;
    }

    private void ApplyFilter()
    {
        var typed = _filter.Text ?? string.Empty;
        var text = typed.Trim();
        _placeholder.IsVisible = typed.Length == 0;
        var visible = _options.ItemsFor is { } itemsFor
            ? itemsFor(text)
            : text.Length == 0
            ? _items
            : _options.IsFreeText?.Invoke(text) == true
            ? []
            : _items.Where(i => i.Label.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                                i.Description.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
        _list.ItemsSource = visible;
        var current = visible.FirstOrDefault(i => i.IsCurrent);
        _list.SelectedItem = text.Length == 0 && current is not null ? current : visible.Count > 0 ? visible[0] : null;
        if (_list.SelectedIndex >= 0)
        {
            _list.ScrollIntoView(_list.SelectedIndex);
        }
    }

    private void OnFilterTextInput(object? sender, TextInputEventArgs e)
    {
        // Drive letters and similar hotkeys accept at once while nothing has been typed.
        if (string.IsNullOrEmpty(_filter.Text) && e.Text is { Length: 1 } typed &&
            _items.FirstOrDefault(i => i.Hotkey is { } hotkey && char.ToUpperInvariant(hotkey) == char.ToUpperInvariant(typed[0])) is { } item)
        {
            e.Handled = true;
            Complete(new QuickPickResult(item, string.Empty));
        }
    }

    private void Select(int index)
    {
        _list.SelectedIndex = index;
        _list.ScrollIntoView(index);
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        var count = _list.ItemCount;
        var text = _filter.Text ?? string.Empty;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                Select((_list.SelectedIndex + 1) % count);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                Select((_list.SelectedIndex - 1 + count) % count);
                e.Handled = true;
                break;
            case Key.PageDown when count > 0:
                Select(Math.Min(count - 1, _list.SelectedIndex + 10));
                e.Handled = true;
                break;
            case Key.PageUp when count > 0:
                Select(Math.Max(0, _list.SelectedIndex - 10));
                e.Handled = true;
                break;
            case Key.Enter:
                AcceptSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                Complete(null);
                e.Handled = true;
                break;
            case Key.Delete when text.Length == 0 && _options.OnDelete is { } onDelete && _list.SelectedItem is QuickPickItem item:
                onDelete(item);
                e.Handled = true;
                break;
            case var key when _options.CommandKeys.Contains(key) && e.KeyModifiers == KeyModifiers.None && _list.SelectedItem is QuickPickItem selected:
                Complete(new QuickPickResult(selected, text, key));
                e.Handled = true;
                break;
        }
    }

    private void AcceptSelected()
    {
        var text = _filter.Text ?? string.Empty;
        if (_options.IsFreeText?.Invoke(text.Trim()) == true)
        {
            Complete(new QuickPickResult(null, text));
        }
        else if (_list.SelectedItem is QuickPickItem item && _list.ItemCount > 0 && !(_options.AllowFreeText && _items.Count == 0))
        {
            Complete(new QuickPickResult(item, text));
        }
        else if (_options.AllowFreeText)
        {
            Complete(new QuickPickResult(null, text));
        }
    }

    private void Complete(QuickPickResult? result)
    {
        _result.TrySetResult(result);
        if (_popup is not null)
        {
            _popup.IsOpen = false;
        }
    }
}
