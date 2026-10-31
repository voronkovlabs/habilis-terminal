using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TerminalCommander.App.Theming;

namespace TerminalCommander.App.Panels;

/// <summary>
/// Empty file panel for the walking skeleton: shows its title and the current directory,
/// and draws a focus border like the VS Code Explorer. Replaced by the real panel in stage 4.
/// </summary>
public sealed class PlaceholderPanel : Border
{
    private readonly TextBlock _title;
    private readonly TextBlock _path;
    private readonly TextBlock _message;
    private bool _isActive;

    public PlaceholderPanel(string title)
    {
        Focusable = true;
        FocusVisualStyle = null;
        Background = new SolidColorBrush(PlaceholderTheme.SideBarBackground);
        BorderThickness = new Thickness(1);
        BorderBrush = new SolidColorBrush(PlaceholderTheme.SideBarBorder);
        Padding = new Thickness(12, 8, 12, 8);

        _title = new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(PlaceholderTheme.SideBarForeground),
        };
        _path = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(PlaceholderTheme.DescriptionForeground),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _message = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(PlaceholderTheme.ErrorForeground),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(_path);
        stack.Children.Add(_message);
        Child = stack;
    }

    public string DirectoryPath
    {
        get => _path.Text;
        set => _path.Text = value;
    }

    /// <summary>An error or notice shown under the path; null hides it.</summary>
    public string? Message
    {
        get => _message.Visibility == Visibility.Visible ? _message.Text : null;
        set
        {
            _message.Text = value ?? string.Empty;
            _message.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>The active panel of the pair; it keeps its highlight while focus is in the terminal.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            UpdateBorder();
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        UpdateBorder();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        UpdateBorder();
    }

    private void UpdateBorder()
    {
        var color = IsKeyboardFocused
            ? PlaceholderTheme.FocusBorder
            : PlaceholderTheme.SideBarBorder;
        BorderBrush = new SolidColorBrush(color);
        _title.Opacity = _isActive ? 1.0 : 0.6;
    }
}
