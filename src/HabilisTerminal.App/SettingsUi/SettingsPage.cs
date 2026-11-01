using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HabilisTerminal.App.Theming;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Settings;

namespace HabilisTerminal.App.SettingsUi;

/// <summary>
/// The Settings page (SPEC §14), modelled on the VS Code Settings editor: a search box, the category list on the left,
/// every setting on the right with its title, description and a control by type, a bar marking modified values,
/// "Reset to default", and "Open settings.json". Built from <see cref="SettingsCatalog"/>, the same source as the schema.
/// </summary>
public sealed class SettingsPage : DockPanel, IPage
{
    private readonly SettingsStore _store;
    private readonly IReadOnlyList<string> _themes;
    private readonly TextBox _search = new() { Width = 520, Margin = new Thickness(0, 0, 12, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _searchPlaceholder = new() { Text = "Search settings", IsHitTestVisible = false, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _categories = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly StackPanel _list = new() { Margin = new Thickness(24, 12, 32, 32) };
    private readonly ScrollViewer _scroll;
    private readonly List<Row> _rows = [];
    private readonly Dictionary<string, FrameworkElement> _headings = [];

    public SettingsPage(SettingsStore store, IReadOnlyList<string> themes)
    {
        _store = store;
        _themes = themes;
        LastChildFill = true;
        SetResourceReference(BackgroundProperty, "Habi.Background");

        // Header: search and "Open settings.json".
        var searchHost = new Grid();
        searchHost.Children.Add(_search);
        searchHost.Children.Add(_searchPlaceholder);
        _searchPlaceholder.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Placeholder");
        _search.TextChanged += (_, _) => ApplyFilter();
        var openJson = new Button { Content = "Open settings.json", HorizontalAlignment = HorizontalAlignment.Right };
        openJson.Click += (_, _) => OpenJsonRequested?.Invoke();
        var location = Text(store.FilePath, 12, "Habi.Description");
        location.Margin = new Thickness(0, 0, 12, 0);
        var header = new DockPanel { Margin = new Thickness(16, 10, 16, 10) };
        SetDock(openJson, Dock.Right);
        SetDock(location, Dock.Right);
        header.Children.Add(openJson);
        header.Children.Add(location);
        header.Children.Add(searchHost);
        var headerBorder = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        headerBorder.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");
        SetDock(headerBorder, Dock.Top);
        Children.Add(headerBorder);

        // Category tree.
        var categoryBorder = new Border { Child = _categories, Width = 200, BorderThickness = new Thickness(0, 0, 1, 0) };
        categoryBorder.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");
        SetDock(categoryBorder, Dock.Left);
        Children.Add(categoryBorder);

        foreach (var category in SettingsCatalog.Categories)
        {
            var label = Text(category, 13, "Habi.Foreground");
            var item = new Border { Child = label, Padding = new Thickness(20, 4, 8, 4), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            item.MouseLeftButtonUp += (_, _) => ScrollTo(category);
            _categories.Children.Add(item);

            var heading = Text(category, 22, "Habi.Foreground");
            heading.FontWeight = FontWeights.SemiBold;
            heading.Margin = new Thickness(0, _headings.Count == 0 ? 0 : 24, 0, 8);
            _headings[category] = heading;
            _list.Children.Add(heading);
            foreach (var definition in SettingsCatalog.All.Where(d => d.Category == category))
            {
                var row = new Row(this, definition);
                _rows.Add(row);
                _list.Children.Add(row.Root);
            }
        }

        _scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        _scroll.ScrollChanged += (_, _) => HighlightVisibleCategory();
        Children.Add(_scroll);

        // The window turns WPF keyboard navigation off; on this page Tab moves between the controls.
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        PreviewKeyDown += OnKeyDown;
        Refresh();
    }

    public event Action? CloseRequested;

    /// <summary>A value changed in the page: (key, value), null meaning "reset to default".</summary>
    public event Action<string, object?>? ValueChanged;

    public event Action? OpenJsonRequested;

    public void FocusContent()
    {
        _search.Focus();
        Keyboard.Focus(_search);
    }

    /// <summary>Re-reads values from the store: after a change here or an edit of settings.json elsewhere.</summary>
    public void Refresh()
    {
        foreach (var row in _rows)
        {
            row.Update(_store.Current);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (_search.IsKeyboardFocused && _search.Text.Length > 0)
            {
                _search.Clear();
                return;
            }

            CloseRequested?.Invoke();
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            FocusContent();
            _search.SelectAll();
        }
    }

    private void ApplyFilter()
    {
        var text = _search.Text.Trim();
        _searchPlaceholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var row in _rows)
        {
            var d = row.Definition;
            var match = text.Length == 0 ||
                d.Key.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                d.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                d.Description.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                d.Category.Contains(text, StringComparison.CurrentCultureIgnoreCase);
            row.Root.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var (category, heading) in _headings)
        {
            heading.Visibility = _rows.Any(r => r.Definition.Category == category && r.Root.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ScrollTo(string category)
    {
        if (_headings.TryGetValue(category, out var heading) && heading.IsVisible)
        {
            var offset = heading.TransformToAncestor(_list).Transform(new Point(0, 0)).Y;
            _scroll.ScrollToVerticalOffset(offset);
        }
    }

    private void HighlightVisibleCategory()
    {
        string? current = null;
        foreach (var (category, heading) in _headings)
        {
            if (heading.IsVisible && heading.TransformToAncestor(_list).Transform(new Point(0, 0)).Y <= _scroll.VerticalOffset + 8)
            {
                current = category;
            }
        }

        current ??= _headings.Keys.FirstOrDefault();
        for (var i = 0; i < _categories.Children.Count; i++)
        {
            var item = (Border)_categories.Children[i];
            var label = (TextBlock)item.Child;
            label.FontWeight = SettingsCatalog.Categories[i] == current ? FontWeights.SemiBold : FontWeights.Normal;
            label.SetResourceReference(TextBlock.ForegroundProperty, SettingsCatalog.Categories[i] == current ? "Habi.Foreground" : "Habi.Description");
        }
    }

    private void Commit(SettingDefinition definition, object? value) => ValueChanged?.Invoke(definition.Key, value);

    private static TextBlock Text(string text, double size, string brushKey)
    {
        var block = new TextBlock { Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = size, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    /// <summary>One setting: modified bar, title, description, control, reset link and validation message.</summary>
    private sealed class Row
    {
        private readonly SettingsPage _page;
        private readonly Rectangle _modifiedBar = new() { Width = 3, Margin = new Thickness(0, 0, 12, 0) };
        private readonly TextBlock _error = Text(string.Empty, 12, "Habi.Error");
        private readonly Button _reset = new() { Content = "Reset to default", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        private readonly CheckBox? _check;
        private readonly TextBox? _textBox;
        private readonly Button? _choice;
        private bool _updating;

        public Row(SettingsPage page, SettingDefinition definition)
        {
            _page = page;
            Definition = definition;
            _modifiedBar.SetResourceReference(Shape.FillProperty, "Habi.Accent");

            var title = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 13, TextWrapping = TextWrapping.Wrap };
            title.Inlines.Add(new System.Windows.Documents.Run(definition.Category + ": "));
            title.Inlines.Add(new System.Windows.Documents.Run(definition.Title) { FontWeight = FontWeights.SemiBold });
            title.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Foreground");
            title.ToolTip = definition.Key;

            var description = Text(definition.Description + (definition.RequiresRestart ? " Applies after restart." : string.Empty), 13, "Habi.Description");
            description.Margin = new Thickness(0, 4, 0, 6);

            var content = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
            content.Children.Add(title);

            switch (definition.Kind)
            {
                case SettingKind.Toggle:
                    // VS Code: the description sits next to the checkbox.
                    _check = new CheckBox { Content = description.Text, Margin = new Thickness(0, 6, 0, 0) };
                    _check.Click += (_, _) => _page.Commit(Definition, _check.IsChecked == true);
                    content.Children.Add(_check);
                    break;
                case SettingKind.Choice:
                    content.Children.Add(description);
                    _choice = ChoiceButton(definition.Values!, null);
                    content.Children.Add(_choice);
                    break;
                default:
                    content.Children.Add(description);
                    _textBox = new TextBox { Width = definition.Kind == SettingKind.Number ? 120 : 360, HorizontalAlignment = HorizontalAlignment.Left };
                    _textBox.LostKeyboardFocus += (_, _) => CommitText();
                    _textBox.KeyDown += (_, e) =>
                    {
                        if (e.Key == Key.Enter)
                        {
                            e.Handled = true;
                            CommitText();
                        }
                    };
                    if (definition.Values is { } suggestions)
                    {
                        // Free text with suggestions (themes): the button lists the known names.
                        var line = new StackPanel { Orientation = Orientation.Horizontal };
                        line.Children.Add(_textBox);
                        var pick = ChoiceButton(definition.Key == "theme" ? _page._themes : suggestions, "…");
                        pick.Margin = new Thickness(6, 0, 0, 0);
                        line.Children.Add(pick);
                        content.Children.Add(line);
                    }
                    else
                    {
                        content.Children.Add(_textBox);
                    }

                    break;
            }

            _error.Margin = new Thickness(0, 4, 0, 0);
            _error.Visibility = Visibility.Collapsed;
            content.Children.Add(_error);
            _reset.Click += (_, _) => _page.Commit(Definition, null);
            content.Children.Add(_reset);

            var layout = new DockPanel();
            DockPanel.SetDock(_modifiedBar, Dock.Left);
            layout.Children.Add(_modifiedBar);
            layout.Children.Add(content);
            Root = new Border { Child = layout, Padding = new Thickness(0, 10, 0, 10) };
        }

        public SettingDefinition Definition { get; }

        public Border Root { get; }

        public void Update(AppSettings settings)
        {
            _updating = true;
            var value = settings.GetValue(Definition.Key);
            var modified = settings.IsModified(Definition.Key);
            _modifiedBar.Visibility = modified ? Visibility.Visible : Visibility.Hidden;
            _reset.Visibility = modified ? Visibility.Visible : Visibility.Collapsed;
            if (_check is not null)
            {
                _check.IsChecked = (bool)value;
            }

            if (_textBox is not null && !_textBox.IsKeyboardFocused)
            {
                _textBox.Text = Convert.ToString(value, CultureInfo.InvariantCulture);
                _error.Visibility = Visibility.Collapsed;
            }

            if (_choice is not null)
            {
                _choice.Content = ChoiceContent((string)value);
            }

            _updating = false;
        }

        private void CommitText()
        {
            if (_updating || _textBox is null)
            {
                return;
            }

            var text = _textBox.Text.Trim();
            object value;
            if (Definition.Kind == SettingKind.Number)
            {
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < Definition.Minimum || number > Definition.Maximum)
                {
                    _error.Text = $"Enter a whole number from {Definition.Minimum} to {Definition.Maximum}.";
                    _error.Visibility = Visibility.Visible;
                    return;
                }

                value = number;
            }
            else
            {
                value = text;
            }

            _error.Visibility = Visibility.Collapsed;
            if (!Equals(value, _page._store.Current.GetValue(Definition.Key)))
            {
                _page.Commit(Definition, value);
            }
        }

        /// <summary>A drop-down button (WPF combo boxes do not take the theme): opens a quick pick with the values.</summary>
        private Button ChoiceButton(IReadOnlyList<string> values, string? fixedLabel)
        {
            var button = new Button { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = fixedLabel is null ? 200 : 0, HorizontalContentAlignment = HorizontalAlignment.Left };
            if (fixedLabel is not null)
            {
                button.Content = fixedLabel;
            }

            button.SetResourceReference(Control.BackgroundProperty, "Habi.InputBackground");
            button.SetResourceReference(Control.ForegroundProperty, "Habi.Foreground");
            button.SetResourceReference(Control.BorderBrushProperty, "Habi.InputBorder");
            button.Click += async (_, _) =>
            {
                var current = Convert.ToString(_page._store.Current.GetValue(Definition.Key), CultureInfo.InvariantCulture);
                var items = values.Select(v => new QuickPickItem { Label = v, Tag = v, IsCurrent = string.Equals(v, current, StringComparison.OrdinalIgnoreCase) }).ToList();
                var result = await QuickPick.ShowAsync(_page, items, new QuickPickOptions { Title = Definition.Title, Placeholder = Definition.Key });
                button.Focus();
                if (result?.Item?.Tag is string chosen)
                {
                    _page.Commit(Definition, chosen);
                }
            };
            return button;
        }

        private static DockPanel ChoiceContent(string value)
        {
            var panel = new DockPanel { LastChildFill = true, MinWidth = 180 };
            var chevron = new System.Windows.Shapes.Path { Data = Codicon.GeometryOf(Codicon.ArrowDown), Width = 10, Height = 10, Stretch = Stretch.Uniform, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            chevron.SetResourceReference(Shape.FillProperty, "Habi.Foreground");
            DockPanel.SetDock(chevron, Dock.Right);
            panel.Children.Add(chevron);
            panel.Children.Add(new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }
    }
}
