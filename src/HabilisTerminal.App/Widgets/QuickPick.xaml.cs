using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

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
public sealed partial class QuickPick : UserControl
{
    private readonly IReadOnlyList<QuickPickItem> _items;
    private readonly QuickPickOptions _options;
    private readonly TaskCompletionSource<QuickPickResult?> _result = new();
    private Popup? _popup;

    private QuickPick(IReadOnlyList<QuickPickItem> items, QuickPickOptions options)
    {
        InitializeComponent();
        _items = items;
        _options = options;
        TitleText.Text = options.Title;
        TitleText.Visibility = string.IsNullOrEmpty(options.Title) ? Visibility.Collapsed : Visibility.Visible;
        Placeholder.Text = options.Placeholder;
        List.Visibility = items.Count == 0 && options.ItemsFor is null ? Visibility.Collapsed : Visibility.Visible;

        Filter.Text = options.InitialText;
        Filter.TextChanged += (_, _) => ApplyFilter();
        Filter.PreviewKeyDown += OnFilterKeyDown;
        Filter.PreviewTextInput += OnFilterTextInput;
        List.MouseDoubleClick += (_, _) => AcceptSelected();
        List.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is FrameworkElement { DataContext: QuickPickItem })
            {
                AcceptSelected();
            }
        };
        ApplyFilter();
    }

    public static Task<QuickPickResult?> ShowAsync(FrameworkElement target, IReadOnlyList<QuickPickItem> items, QuickPickOptions options)
    {
        var pick = new QuickPick(items, options);
        var popup = new Popup
        {
            Child = pick,
            PlacementTarget = target,
            Placement = PlacementMode.Relative,
            AllowsTransparency = true,
            StaysOpen = false,
            Focusable = true,
        };
        UiZoom.Apply(popup);

        // Centered horizontally near the top of the target, like the VS Code quick pick.
        if (options.OverTarget)
        {
            const double Padding = 7; // the frame around the filter box, so that it lies over the target
            pick.Width = Math.Max(pick.Width, target.ActualWidth + (2 * Padding));
            popup.HorizontalOffset = (target.ActualWidth - pick.Width) / 2;
            popup.VerticalOffset = -Padding;
        }
        else
        {
            popup.HorizontalOffset = Math.Max(0, (target.ActualWidth - pick.Width) / 2);
            popup.VerticalOffset = 8;
        }

        popup.Closed += (_, _) => pick._result.TrySetResult(null);
        popup.Opened += (_, _) =>
        {
            pick.Filter.Focus();
            Keyboard.Focus(pick.Filter);

            // Like VS Code: an initial value is selected, so typing replaces it.
            pick.Filter.SelectAll();
        };

        pick._popup = popup;
        PopupFocus.Open(popup);
        return pick._result.Task;
    }

    private void ApplyFilter()
    {
        var text = Filter.Text.Trim();
        Placeholder.Visibility = Filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var visible = _options.ItemsFor is { } itemsFor
            ? itemsFor(text)
            : text.Length == 0
            ? _items
            : _options.IsFreeText?.Invoke(text) == true
            ? []
            : _items.Where(i => i.Label.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                                i.Description.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
        List.ItemsSource = visible;
        var current = visible.FirstOrDefault(i => i.IsCurrent);
        List.SelectedItem = text.Length == 0 && current is not null ? current : visible.Count > 0 ? visible[0] : null;
        if (List.SelectedItem is not null)
        {
            List.ScrollIntoView(List.SelectedItem);
        }
    }

    private void OnFilterTextInput(object sender, TextCompositionEventArgs e)
    {
        // Drive letters and similar hotkeys accept at once while nothing has been typed.
        if (Filter.Text.Length == 0 && e.Text.Length == 1 &&
            _items.FirstOrDefault(i => i.Hotkey is { } hotkey && char.ToUpperInvariant(hotkey) == char.ToUpperInvariant(e.Text[0])) is { } item)
        {
            e.Handled = true;
            Complete(new QuickPickResult(item, string.Empty));
        }
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        var count = List.Items.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                List.SelectedIndex = (List.SelectedIndex + 1) % count;
                List.ScrollIntoView(List.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                List.SelectedIndex = (List.SelectedIndex - 1 + count) % count;
                List.ScrollIntoView(List.SelectedItem);
                e.Handled = true;
                break;
            case Key.PageDown when count > 0:
                List.SelectedIndex = Math.Min(count - 1, List.SelectedIndex + 10);
                List.ScrollIntoView(List.SelectedItem);
                e.Handled = true;
                break;
            case Key.PageUp when count > 0:
                List.SelectedIndex = Math.Max(0, List.SelectedIndex - 10);
                List.ScrollIntoView(List.SelectedItem);
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
            case Key.Delete when Filter.Text.Length == 0 && _options.OnDelete is { } onDelete && List.SelectedItem is QuickPickItem item:
                onDelete(item);
                e.Handled = true;
                break;
            case var key when _options.CommandKeys.Contains(key) && Keyboard.Modifiers == ModifierKeys.None && List.SelectedItem is QuickPickItem selected:
                Complete(new QuickPickResult(selected, Filter.Text, key));
                e.Handled = true;
                break;
        }
    }

    private void AcceptSelected()
    {
        if (_options.IsFreeText?.Invoke(Filter.Text.Trim()) == true)
        {
            Complete(new QuickPickResult(null, Filter.Text));
        }
        else if (List.SelectedItem is QuickPickItem item && (List.Items.Count > 0 && !(_options.AllowFreeText && _items.Count == 0)))
        {
            Complete(new QuickPickResult(item, Filter.Text));
        }
        else if (_options.AllowFreeText)
        {
            Complete(new QuickPickResult(null, Filter.Text));
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
