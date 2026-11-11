using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using HabilisTerminal.App.Theming;
using HabilisTerminal.Core.Plugins;

namespace HabilisTerminal.App.Plugins;

internal enum PluginRowOrigin
{
    Shipped,
    User,
    Development,
}

internal enum PluginRowState
{
    On,
    Off,
    Failed,
    NotLoaded,
}

/// <summary>A row of the plugin manager.</summary>
internal sealed record PluginRow(string Id, string Name, string? Version, PluginRowOrigin Origin, PluginRowState State, string? Error, string Folder, LoadedPlugin? Running)
{
    public bool CanConfigure => Running?.Instance is { } plugin && PluginManager.Guard(Running, () => plugin.HasConfiguration, false);

    public bool CanRemove => Origin == PluginRowOrigin.User;

    public string OriginText => Origin switch
    {
        PluginRowOrigin.Shipped => "With the app",
        PluginRowOrigin.User => "Installed",
        _ => "Development",
    };

    public string StateText => State switch
    {
        PluginRowState.On => "On",
        PluginRowState.Off => "Off",
        PluginRowState.Failed => "Failed",
        _ => "Not loaded",
    };
}

/// <summary>
/// The plugin manager page (issue #166), keyboard first as the panels are: Up, Down, Home, End, PgUp, PgDn move, Ins
/// installs, Space turns off and on, F8 or Del removes, F5 reloads, Enter configures, Esc closes.
/// </summary>
internal sealed class PluginsPage : DockPanel, IPage
{
    private const double RowHeight = 28;

    private readonly Func<IReadOnlyList<PluginRow>> _rowsSource;
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _install = new() { Content = "Install… (Ins)", Focusable = false, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _toggle = new() { Focusable = false, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _remove = new() { Content = "Remove (F8)", Focusable = false, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _reload = new() { Content = "Reload (F5)", Focusable = false, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _configure = new() { Content = "Configure (Enter)", Focusable = false, Margin = new Thickness(0, 0, 8, 0) };
    private IReadOnlyList<PluginRow> _rows = [];
    private int _selected;
    private bool _busy;

    public PluginsPage(Func<IReadOnlyList<PluginRow>> rows)
    {
        _rowsSource = rows;
        Focusable = true;
        ;
        LastChildFill = true;
        this.SetResourceReference(BackgroundProperty, "Habi.Background");

        var title = new TextBlock { Text = "Plugins", FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Foreground");
        var hint = new TextBlock { Text = "Ins install · Space on or off · F8 remove · F5 reload · Enter configure · Esc close", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Description");
        var header = new DockPanel { Margin = new Thickness(16, 8, 16, 8) };
        SetDock(hint, Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(title);
        var headerBorder = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        headerBorder.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");
        SetDock(headerBorder, Dock.Top);
        Children.Add(headerBorder);

        var columns = Row("Name", "Version", "Source", "State", Brushes.Transparent, null, FontWeight.SemiBold);
        var columnsBorder = new Border { Child = columns, BorderThickness = new Thickness(0, 0, 0, 1) };
        columnsBorder.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");
        SetDock(columnsBorder, Dock.Top);
        Children.Add(columnsBorder);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var button in new[] { _install, _toggle, _remove, _reload, _configure })
        {
            buttons.Children.Add(button);
        }

        var bottom = new StackPanel { Margin = new Thickness(16, 10, 16, 12) };
        bottom.Children.Add(_detail);
        bottom.Children.Add(_message);
        bottom.Children.Add(buttons);
        var bottomBorder = new Border { Child = bottom, BorderThickness = new Thickness(0, 1, 0, 0) };
        bottomBorder.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        bottomBorder.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");
        SetDock(bottomBorder, Dock.Bottom);
        Children.Add(bottomBorder);

        _detail.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Description");
        _message.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Foreground");
        _scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Children.Add(_scroll);

        _install.Click += (_, _) => InstallRequested?.Invoke();
        _toggle.Click += (_, _) => Act(ToggleRequested);
        _remove.Click += (_, _) => Act(RemoveRequested);
        _reload.Click += (_, _) => Act(ReloadRequested);
        _configure.Click += (_, _) => Configure();
        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Refresh();
    }

    public event Action? CloseRequested;

    public event Action? InstallRequested;

    public event Action<PluginRow>? ToggleRequested;

    public event Action<PluginRow>? RemoveRequested;

    public event Action<PluginRow>? ReloadRequested;

    public event Action<PluginRow>? ConfigureRequested;

    private PluginRow? Current => _selected < _rows.Count ? _rows[_selected] : null;

    public void FocusContent()
    {
        Focus();
        this.Focus();
    }

    /// <summary>An action starts; false while another one runs.</summary>
    public bool TryBegin()
    {
        if (_busy)
        {
            return false;
        }

        _busy = true;

        // Unloading waits up to a few seconds for the plugin's code to leave memory.
        _message.Text = "Working…";
        return true;
    }

    /// <summary>The action ended: the list is read again, the selection stays on the same plugin.</summary>
    public void End(string? message)
    {
        _busy = false;
        _message.Text = message ?? string.Empty;
        Refresh();
        FocusContent();
    }

    private void Refresh()
    {
        var id = Current?.Id;
        _rows = _rowsSource();
        var same = _rows.ToList().FindIndex(r => r.Id == id);
        _selected = same >= 0 ? same : Math.Min(_selected, Math.Max(0, _rows.Count - 1));
        _list.Children.Clear();
        for (var i = 0; i < _rows.Count; i++)
        {
            var index = i;
            var row = _rows[i];
            var isSelected = i == _selected;
            var element = Row(row.Name, row.Version ?? "-", row.OriginText, row.StateText, isSelected ? PanelPalette.ActiveSelectionBackground : Brushes.Transparent, row, FontWeight.Normal);
            element.PointerPressed += (_, e) =>
            {
                Select(index);
                FocusContent();
                if (e.ClickCount == 2)
                {
                    Configure();
                }
            };
            _list.Children.Add(element);
        }

        UpdateDetails();
    }

    private static Grid Row(string name, string version, string origin, string state, IBrush background, PluginRow? row, FontWeight weight)
    {
        var grid = new Grid { Height = RowHeight, Background = background };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        var texts = new[] { name, version, origin, state };
        for (var i = 0; i < texts.Length; i++)
        {
            var text = new TextBlock { Text = texts[i], FontSize = 13, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(i == 0 ? 16 : 4, 0, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            if (row is { State: PluginRowState.Failed } && i == 3)
            {
                text.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Error");
            }
            else
            {
                text.SetResourceReference(TextBlock.ForegroundProperty, row is { State: PluginRowState.Off } ? "Habi.Description" : "Habi.Foreground");
            }

            Grid.SetColumn(text, i);
            grid.Children.Add(text);
        }

        return grid;
    }

    private void Select(int index)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var target = Math.Clamp(index, 0, _rows.Count - 1);
        if (_list.Children[_selected] is Grid previous)
        {
            previous.Background = Brushes.Transparent;
        }

        _selected = target;
        if (_list.Children[_selected] is Grid current)
        {
            current.Background = PanelPalette.ActiveSelectionBackground;
            current.BringIntoView();
        }

        UpdateDetails();
    }

    private void UpdateDetails()
    {
        var row = Current;
        _detail.Text = row is null
            ? "No plugins are installed. Ins installs one from a zip or a folder."
            : $"{row.Id}  ·  {row.Folder}{(row.Error is null ? string.Empty : Environment.NewLine + row.Error)}";
        _toggle.Content = row?.State is PluginRowState.On or PluginRowState.Failed ? "Turn off (Space)" : "Turn on (Space)";
        _toggle.IsEnabled = row is not null;
        _reload.IsEnabled = row is not null && row.State != PluginRowState.Off;
        _remove.IsEnabled = row?.CanRemove == true;
        _configure.IsEnabled = row?.CanConfigure == true;
    }

    private void Act(Action<PluginRow>? action)
    {
        if (Current is { } row)
        {
            action?.Invoke(row);
        }
    }

    private void Configure()
    {
        if (Current is { CanConfigure: true } row)
        {
            ConfigureRequested?.Invoke(row);
        }
        else if (Current is { } other)
        {
            _message.Text = other.State == PluginRowState.On ? $"{other.Name} has no settings." : $"{other.Name} is not running.";
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != KeyModifiers.None)
        {
            return;
        }

        var page = Math.Max(1, (int)(_scroll.Viewport.Height / RowHeight) - 1);
        switch (e.Key)
        {
            case Key.Escape:
                CloseRequested?.Invoke();
                break;
            case Key.Up:
                Select(_selected - 1);
                break;
            case Key.Down:
                Select(_selected + 1);
                break;
            case Key.PageUp:
                Select(_selected - page);
                break;
            case Key.PageDown:
                Select(_selected + page);
                break;
            case Key.Home:
                Select(0);
                break;
            case Key.End:
                Select(_rows.Count - 1);
                break;
            case Key.Insert:
                InstallRequested?.Invoke();
                break;
            case Key.Space:
                Act(ToggleRequested);
                break;
            case Key.F8:
            case Key.Delete:
                Act(RemoveRequested);
                break;
            case Key.F5:
                Act(ReloadRequested);
                break;
            case Key.Enter:
                Configure();
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
