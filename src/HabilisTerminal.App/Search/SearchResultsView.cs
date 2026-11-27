using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using HabilisTerminal.Core.Search;

namespace HabilisTerminal.App.Search;

/// <summary>What a key or a click on a result asks for.</summary>
internal enum FindRequest
{
    /// <summary>Enter or a double click: a file goes to its panel, a line opens the viewer there.</summary>
    Open,
    View,
    Edit,
}

/// <summary>
/// The results tab's content (issue #415): a toolbar, a status line and the tree as one list, folders and files
/// folding like Visual Studio's Find Results. The rows are taken from the search a few times a second; new ones are
/// put in place, so the selection and the scroll stay where they are.
/// </summary>
internal sealed class SearchResultsView : DockPanel
{
    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xE8, 0xAB, 0x00));

    private readonly SearchTab _tab;
    private readonly AvaloniaList<FindRow> _rows = [];
    private readonly ListBox _list;
    private readonly TextBlock _status = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly Button _stop;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public SearchResultsView(SearchTab tab)
    {
        _tab = tab;
        LastChildFill = true;
        Focusable = false;
        this.SetResourceReference(BackgroundProperty, "Habi.Background");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Description");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Foreground");

        _stop = Tool("Stop", "Stop the search (Ctrl+C)", () => _tab.Session.Stop());
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        tools.Children.Add(_stop);
        tools.Children.Add(Tool("Modify find…", "The Alt+F7 dialog again, with these options (F7)", () => ModifyRequested?.Invoke()));
        tools.Children.Add(Tool("Panel", "Show the files found as a panel (F2)", () => PanelRequested?.Invoke()));
        tools.Children.Add(Tool("Fold files", "Hide the lines under each file (Gray-)", () => Fold(true)));
        tools.Children.Add(Tool("Unfold all", "Show everything (Gray+)", () => Fold(false)));

        var header = new DockPanel { Margin = new Thickness(10, 4, 10, 2) };
        DockPanel.SetDock(tools, Dock.Right);
        header.Children.Add(tools);
        header.Children.Add(_title);
        header.Children.Add(_status);
        SetDock(header, Dock.Top);
        Children.Add(header);

        var columns = ColumnHeader();
        SetDock(columns, Dock.Top);
        Children.Add(columns);

        _list = new ListBox
        {
            ItemsSource = _rows,
            Background = Brushes.Transparent,
            BorderThickness = default,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new FuncDataTemplate<FindRow>((row, _) => row is null ? new Panel() : Row(row)),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(6, 1)), new Setter(ListBoxItem.MinHeightProperty, 20.0) },
        });
        _list.AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _list.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control { DataContext: FindRow row })
            {
                if (row.Kind == FindRowKind.Folder && row.CanExpand)
                {
                    Toggle(row);
                }
                else
                {
                    Requested?.Invoke(row, FindRequest.Open);
                }
            }
        };
        Children.Add(_list);

        _timer.Tick += (_, _) => Pull();
        Restart();
    }

    /// <summary>Enter, F3, F4 or a double click on a result.</summary>
    public event Action<FindRow, FindRequest>? Requested;

    /// <summary>Esc: back to the console and its panels.</summary>
    public event Action? LeaveRequested;

    public event Action? ModifyRequested;

    public event Action? PanelRequested;

    /// <summary>The tab's texts changed: the strip shows them.</summary>
    public event Action? Updated;

    public FindRow? Selected => _list.SelectedItem as FindRow;

    public void FocusList()
    {
        if (_list.SelectedIndex < 0 && _rows.Count > 0)
        {
            _list.SelectedIndex = 0;
        }

        _list.Focus();
        if (_list.ContainerFromIndex(Math.Max(0, _list.SelectedIndex)) is { } container)
        {
            container.Focus();
        }
    }

    /// <summary>A new search in this tab: the list starts again.</summary>
    public void Restart()
    {
        _rows.Clear();
        _title.Text = _tab.Title;
        _status.Text = _tab.Status;
        _stop.IsEnabled = true;
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Pull()
    {
        var session = _tab.Session;
        var done = session.IsDone;
        foreach (var item in session.Since(_tab.Taken))
        {
            _tab.Tree.Add(item);
            _tab.Taken++;
        }

        if (_tab.Tree.Changed)
        {
            Merge(_tab.Tree.Rows());
        }

        _status.Text = _tab.Status;
        _stop.IsEnabled = !done;
        if (done && _tab.Taken >= session.Count)
        {
            _timer.Stop();
        }

        // The first result is selected as soon as there is one, so PageDown and Enter work at once.
        if (_list.SelectedIndex < 0 && _rows.Count > 0)
        {
            _list.SelectedIndex = 0;
        }

        Updated?.Invoke();
    }

    /// <summary>
    /// New rows into the shown list without starting it again: results only add rows, so the old ones keep their
    /// places and new ones are inserted; a row whose count changed is replaced. A fold rebuilds the list.
    /// </summary>
    private void Merge(IReadOnlyList<FindRow> rows)
    {
        var selected = Selected?.Key;
        if (_rows.Count == 0)
        {
            _rows.AddRange(rows);
            return;
        }

        var keys = new HashSet<string>(rows.Count, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            keys.Add(row.Key);
        }

        var j = 0;
        var inOrder = true;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (j < _rows.Count && _rows[j].Key == row.Key)
            {
                if (_rows[j] != row)
                {
                    _rows[j] = row;
                }

                j++;
                continue;
            }

            // A shown row that is gone (folded away): the order no longer holds.
            if (j < _rows.Count && !keys.Contains(_rows[j].Key))
            {
                inOrder = false;
                break;
            }

            _rows.Insert(j, row);
            j++;
        }

        if (!inOrder || j < _rows.Count)
        {
            _rows.Clear();
            _rows.AddRange(rows);
        }

        if (selected is not null && (Selected?.Key != selected))
        {
            var index = IndexOf(selected);
            if (index >= 0)
            {
                _list.SelectedIndex = index;
            }
        }
    }

    private int IndexOf(string key)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (HandleKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// A key that reached the window while the list had lost the focus (a closing dialog gives it back to the console
    /// hidden behind): the list takes the focus again and the key, so it is not lost to the shell.
    /// </summary>
    public bool TakeKey(Key key, KeyModifiers modifiers)
    {
        FocusList();
        var plain = modifiers == KeyModifiers.None;
        var count = _rows.Count;
        var index = Math.Max(0, _list.SelectedIndex);
        var page = Math.Max(1, (int)(_list.Bounds.Height / 20) - 1);
        int? target = key switch
        {
            Key.Down when plain => Math.Min(count - 1, index + 1),
            Key.Up when plain => Math.Max(0, index - 1),
            Key.PageDown when plain => Math.Min(count - 1, index + page),
            Key.PageUp when plain => Math.Max(0, index - page),
            Key.Home when plain => 0,
            Key.End when plain => count - 1,
            _ => null,
        };
        if (target is { } to && count > 0)
        {
            _list.SelectedIndex = to;
            _list.ScrollIntoView(to);

            // The list moves from its focused row: the next key must start from this one.
            _list.ContainerFromIndex(to)?.Focus();
            return true;
        }

        return HandleKey(key, modifiers);
    }

    private bool HandleKey(Key key, KeyModifiers modifiers)
    {
        var row = Selected;
        var plain = modifiers == KeyModifiers.None;
        switch (key)
        {
            case Key.Enter when plain && row is not null:
                if (row.Kind == FindRowKind.Folder && row.CanExpand)
                {
                    Toggle(row);
                }
                else
                {
                    Requested?.Invoke(row, FindRequest.Open);
                }

                break;
            case Key.F3 when plain && row is not null:
                Requested?.Invoke(row, FindRequest.View);
                break;
            case Key.F4 when plain && row is not null:
                Requested?.Invoke(row, FindRequest.Edit);
                break;
            case Key.Left when plain && row is not null:
                if (row.Expanded)
                {
                    Toggle(row);
                }
                else
                {
                    SelectParent(row);
                }

                break;
            case Key.Right when plain && row is not null:
                if (row.CanExpand && !row.Expanded)
                {
                    Toggle(row);
                }
                else if (row.Expanded)
                {
                    _list.SelectedIndex = Math.Min(_rows.Count - 1, _list.SelectedIndex + 1);
                }

                break;
            case Key.Add when plain:
                Fold(false);
                break;
            case Key.Subtract when plain:
                Fold(true);
                break;
            case Key.F7 when plain:
                ModifyRequested?.Invoke();
                break;
            case Key.F2 when plain:
                PanelRequested?.Invoke();
                break;
            case Key.C when modifiers == KeyModifiers.Control && !_tab.Session.IsDone:
                _tab.Session.Stop();
                break;
            case Key.C when modifiers == KeyModifiers.Control && row is not null:
                Widgets.Clipboard.SetText(row.Line is { } line ? $"{row.Item?.Path}({line.Number},{line.Column}): {line.Text}" : row.Item?.Path ?? row.Key);
                break;
            case Key.Escape when plain:
                LeaveRequested?.Invoke();
                break;
            default:
                return false;
        }

        return true;
    }

    private void Toggle(FindRow row)
    {
        _tab.Tree.SetFolded(row.Key, row.Expanded);
        Merge(_tab.Tree.Rows());
        var index = IndexOf(row.Key);
        if (index >= 0)
        {
            _list.SelectedIndex = index;
        }
    }

    private void Fold(bool files)
    {
        var key = Selected?.Line is not null ? Selected.Key[..Selected.Key.LastIndexOf(':')] : Selected?.Key;
        _tab.Tree.FoldFiles(files);
        Merge(_tab.Tree.Rows());
        if (key is not null && IndexOf(key) is var index and >= 0)
        {
            _list.SelectedIndex = index;
            _list.ScrollIntoView(index);
        }
    }

    private void SelectParent(FindRow row)
    {
        for (var i = _list.SelectedIndex - 1; i >= 0; i--)
        {
            if (_rows[i].Depth < row.Depth)
            {
                _list.SelectedIndex = i;
                return;
            }
        }
    }

    private static Grid ColumnHeader()
    {
        var grid = Columns(new Thickness(16, 2, 16, 2));
        grid.Children.Add(Cell("Code", 0, "Habi.Description"));
        grid.Children.Add(Cell("File", 1, "Habi.Description"));
        grid.Children.Add(Cell("Line", 2, "Habi.Description", right: true));
        grid.Children.Add(Cell("Col", 3, "Habi.Description", right: true));
        return grid;
    }

    private static Grid Columns(Thickness margin) => new()
    {
        Margin = margin,
        ColumnDefinitions = new ColumnDefinitions("*,220,56,48"),
    };

    private static Grid Row(FindRow row)
    {
        var grid = Columns(new Thickness(0));
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        var chevron = row.CanExpand ? (row.Expanded ? "▾ " : "▸ ") : "   ";
        text.Inlines!.Add(new Run(new string(' ', row.Depth * 3) + chevron) { FontFamily = new FontFamily(Theming.Fonts.Mono) });
        if (row.Line is { } line)
        {
            var mono = new FontFamily(Theming.Fonts.Mono);
            var start = Math.Clamp(line.MatchStart, 0, line.Text.Length);
            var length = Math.Clamp(line.MatchLength, 0, line.Text.Length - start);
            text.Inlines.Add(new Run(line.Text[..start]) { FontFamily = mono });
            text.Inlines.Add(new Run(line.Text.Substring(start, length)) { FontFamily = mono, Background = MatchBrush });
            text.Inlines.Add(new Run(line.Text[(start + length)..]) { FontFamily = mono });
        }
        else
        {
            var name = new Run(row.Text);
            if (row.Kind == FindRowKind.Folder)
            {
                name.FontWeight = FontWeight.SemiBold;
            }

            text.Inlines.Add(name);

            // A file found by its name alone has nothing to count.
            if (row.Kind == FindRowKind.Folder || row.Item?.Lines is not null)
            {
                var count = new Run($"  ({row.Count:N0})");
                count.SetResourceReference(TextElement.ForegroundProperty, "Habi.Description");
                text.Inlines.Add(count);
            }
        }

        grid.Children.Add(text);
        if (row.Line is { } at)
        {
            grid.Children.Add(Cell(Path.GetFileName(row.Item?.Path ?? string.Empty), 1, "Habi.Description"));
            grid.Children.Add(Cell(at.Number.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), 2, "Habi.Description", right: true));
            grid.Children.Add(Cell(at.Column.ToString(System.Globalization.CultureInfo.CurrentCulture), 3, "Habi.Description", right: true));
        }

        return grid;
    }

    private static TextBlock Cell(string text, int column, string brush, bool right = false)
    {
        var cell = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(8, 0, 0, 0),
        };
        cell.SetResourceReference(TextBlock.ForegroundProperty, brush);
        Grid.SetColumn(cell, column);
        return cell;
    }

    private static Button Tool(string text, string tip, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 1), FontSize = 12, Focusable = false };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }
}
