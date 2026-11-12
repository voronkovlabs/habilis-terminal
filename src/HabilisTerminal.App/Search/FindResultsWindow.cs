using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using HabilisTerminal.Core.Search;

namespace HabilisTerminal.App.Search;

internal enum FindAction
{
    Close,
    GoTo,
    View,
    Edit,
    Panel,
    NewSearch,
}

/// <summary>
/// Results of Alt+F7 as they come (issue #198), in the style of the app's dialogs: Enter goes to the file, F3 views it,
/// F4 edits it, Panel puts the list in the panel, Stop ends the search and Esc closes it.
/// </summary>
internal sealed class FindResultsWindow : Widgets.ModalWindow
{
    /// <summary>Rows without the Fluent padding, the selection in the theme's colours.</summary>
    private static Avalonia.Styling.Style ItemStyle()
    {
        var style = new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.OfType<ListBoxItem>(x));
        style.Setters.Add(new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(6, 1, 6, 1)));
        style.Setters.Add(new Avalonia.Styling.Setter(ListBoxItem.MinHeightProperty, 0d));
        return style;
    }

    private readonly FindSession _session;
    private readonly ObservableCollection<string> _rows = [];
    private readonly List<FoundItem> _items = [];
    private readonly ListBox _list;
    private readonly TextBlock _status = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _stop = new() { Content = "_Stop" };
    private readonly DispatcherTimer _timer;
    private FindAction _action;

    private FindResultsWindow(FindSession session)
    {
        _session = session;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;
        Width = 820 * Widgets.UiZoom.Factor;
        Height = 560 * Widgets.UiZoom.Factor;

        var title = new TextBlock { Text = TitleOf(session.Options), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 10), TextTrimming = TextTrimming.CharacterEllipsis };
        title.Classes.Add("label");
        _status.Classes.Add("description");

        _list = new ListBox
        {
            ItemsSource = _rows,
            BorderThickness = new Thickness(1),
            FontFamily = new FontFamily(Theming.Fonts.Mono),
            FontSize = 13,
        };
        _list.Styles.Add(ItemStyle());
        _list.SetResourceReference(BackgroundProperty, "Habi.InputBackground");
        _list.SetResourceReference(BorderBrushProperty, "Habi.WidgetBorder");
        _list.DoubleTapped += (_, _) => Finish(FindAction.GoTo);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (label, action) in new[] { ("_Go to", FindAction.GoTo), ("_View", FindAction.View), ("_Edit", FindAction.Edit), ("_Panel", FindAction.Panel), ("_New search", FindAction.NewSearch) })
        {
            var button = new Button { Content = label, IsDefault = action == FindAction.GoTo, Margin = new Thickness(6, 0, 0, 0) };
            button.Click += (_, _) => Finish(action);
            buttons.Children.Add(button);
        }

        _stop.Margin = new Thickness(6, 0, 0, 0);
        _stop.Click += (_, _) => _session.Stop();
        buttons.Children.Add(_stop);
        var close = new Button { Content = "Close", IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
        close.Click += (_, _) => Finish(FindAction.Close);
        buttons.Children.Add(close);

        var layout = new DockPanel();
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_status, Dock.Bottom);
        layout.Children.Add(title);
        layout.Children.Add(buttons);
        layout.Children.Add(_status);
        layout.Children.Add(_list);

        var frame = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 16, 20, 16),
            Margin = new Thickness(16),
            Child = layout,
            BoxShadow = BoxShadows.Parse("0 4 16 0 #73000000"),
        };
        frame.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        frame.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");
        frame.PointerPressed += (_, e) =>
        {
            if (e.Source is Border or TextBlock && e.Source is not ListBoxItem)
            {
                BeginMoveDrag(e);
            }
        };
        Content = frame;
        Widgets.UiZoom.Attach(this);

        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Pull();
        Loaded += (_, _) =>
        {
            Pull();
            _list.SelectedIndex = Math.Clamp(_session.Selected, 0, Math.Max(0, _rows.Count - 1));
            FocusRow();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>What the user chose, with the item when the action needs one.</summary>
    public static (FindAction Action, FoundItem? Item) Show(Window owner, FindSession session)
    {
        var window = new FindResultsWindow(session) { ModalOwner = owner };
        window.ShowModal();
        var item = window._list.SelectedIndex is >= 0 and var index && index < window._items.Count ? window._items[index] : null;
        session.Selected = Math.Max(0, window._list.SelectedIndex);
        return (window._action, item);
    }

    private static string TitleOf(FindOptions options) =>
        string.IsNullOrEmpty(options.Text) ? $"Find file: {options.Masks}" : $"Find file: {options.Masks} containing \"{options.Text}\"";

    private void Pull()
    {
        var added = _session.Since(_items.Count);
        foreach (var item in added)
        {
            _items.Add(item);
            _rows.Add((item.IsFolder ? "[folder]  " : string.Empty) + _session.Display(item));
        }

        if (added.Count > 0 && _list.SelectedIndex < 0)
        {
            _list.SelectedIndex = 0;
            FocusRow();
        }

        var count = _items.Count == 1 ? "1 found" : $"{_items.Count:N0} found";
        var unreadable = _session.Unreadable > 0 ? $", {_session.Unreadable:N0} folders or archives could not be read" : string.Empty;
        if (_session.CacheHits > 0)
        {
            unreadable += $", {_session.CacheHits:N0} unchanged files answered from the last search";
        }
        _status.Text = !_session.IsDone ? $"{count}. Searching {_session.Folder}"
            : _session.Stopped ? $"{count}. Stopped{unreadable}." : $"{count}. Done{unreadable}.";
        _stop.IsEnabled = !_session.IsDone;
        if (_session.IsDone && _session.Count == _items.Count)
        {
            _timer.Stop();
        }
    }

    private void FocusRow()
    {
        if (_list.SelectedIndex < 0)
        {
            _list.Focus();
            return;
        }

        _list.ScrollIntoView(_list.SelectedIndex);
        _list.UpdateLayout();
        if (_list.ContainerFromIndex(_list.SelectedIndex) is ListBoxItem row)
        {
            row.Focus();
        }
        else
        {
            _list.Focus();
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                Finish(FindAction.Close);
                break;
            case Key.F3:
                Finish(FindAction.View);
                break;
            case Key.F4:
                Finish(FindAction.Edit);
                break;
            case Key.Enter:
                Finish(FindAction.GoTo);
                break;
            default:
                // Letters press the buttons, as in the app's other dialogs.
                var letter = e.Key.ToString();
                foreach (var button in FindButtons(this))
                {
                    var label = button.Content as string ?? string.Empty;
                    var marker = label.IndexOf('_', StringComparison.Ordinal);
                    if (marker >= 0 && marker + 1 < label.Length && letter.Length == 1 && char.ToUpperInvariant(label[marker + 1]) == letter[0] && button.IsEnabled)
                    {
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        e.Handled = true;
                    }
                }

                return;
        }

        e.Handled = true;
    }

    private void Finish(FindAction action)
    {
        if (action is FindAction.GoTo or FindAction.View or FindAction.Edit or FindAction.Panel && _items.Count == 0)
        {
            return;
        }

        _action = action;
        Close();
    }

    private static IEnumerable<Button> FindButtons(Avalonia.Visual root)
    {
        foreach (var child in Avalonia.VisualTree.VisualExtensions.GetVisualChildren(root))
        {
            if (child is Button button)
            {
                yield return button;
            }

            foreach (var inner in FindButtons(child))
            {
                yield return inner;
            }
        }
    }
}
