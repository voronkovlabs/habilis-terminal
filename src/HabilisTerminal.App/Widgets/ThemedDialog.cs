using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace HabilisTerminal.App.Widgets;

/// <param name="Label">Button text; <c>_</c> marks the access key (Alt+letter or the letter alone).</param>
public sealed record DialogButton<T>(string Label, T Result, bool IsDefault = false, bool IsCancel = false);

/// <summary>
/// A modal dialog in the style of VS Code dialogs: borderless, rounded, themed from <c>Habi.*</c> resources,
/// centered on its owner. Enter presses the default button, Esc the cancel button, access keys work without Alt.
/// </summary>
internal sealed class DialogWindow<T> : Window
{
    private T _result;

    internal DialogWindow(string title, UIElement body, IReadOnlyList<DialogButton<T>> buttons, T cancelResult)
    {
        _result = cancelResult;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;

        var titleText = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        titleText.SetResourceReference(TextBlock.StyleProperty, "Habi.LabelStyle");

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var definition in buttons)
        {
            var button = new Button { Content = definition.Label, IsDefault = definition.IsDefault, IsCancel = definition.IsCancel };
            button.Click += (_, _) =>
            {
                _result = definition.Result;
                DialogResult = true;
            };
            buttonRow.Children.Add(button);
        }

        var layout = new StackPanel { MinWidth = 420, MaxWidth = 640 };
        layout.Children.Add(titleText);
        layout.Children.Add(body);
        layout.Children.Add(buttonRow);

        var frame = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 16, 20, 16),
            Margin = new Thickness(16),
            Child = layout,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black },
        };
        frame.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        frame.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");
        Content = frame;
        UiZoom.Attach(this);

        // Access keys without Alt, as in Far dialogs (only when focus is not in a text box).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
                return;
            }

            if (Keyboard.FocusedElement is TextBox || Keyboard.Modifiers != ModifierKeys.None)
            {
                return;
            }

            var letter = e.Key.ToString();
            foreach (var button in buttonRow.Children.OfType<Button>())
            {
                var label = (string)button.Content;
                var marker = label.IndexOf('_', StringComparison.Ordinal);
                if (marker >= 0 && marker + 1 < label.Length && letter.Length == 1 &&
                    char.ToUpperInvariant(label[marker + 1]) == letter[0])
                {
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    e.Handled = true;
                    return;
                }
            }
        };

        // Dragging by the frame moves the dialog.
        frame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is Border or TextBlock)
            {
                DragMove();
            }
        };
    }

    internal T Result => _result;
}

public static class ThemedDialog
{
    /// <summary>Shows a dialog modally; returns the chosen button's result or <paramref name="cancelResult"/>.</summary>
    /// <param name="owner">The window to centre on; null before the main window exists (centred on the screen, in the taskbar).</param>
    public static T Show<T>(Window? owner, string title, UIElement body, IReadOnlyList<DialogButton<T>> buttons, T cancelResult, UIElement? focus = null)
    {
        var dialog = new DialogWindow<T>(title, body, buttons, cancelResult) { Owner = owner };
        if (owner is null)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;
            dialog.Title = title;
        }

        dialog.Loaded += (_, _) =>
        {
            var target = focus ?? (UIElement?)FindDefault(dialog) ?? dialog;
            target.Focus();
            if (target is TextBox textBox)
            {
                textBox.SelectAll();
            }
        };
        return dialog.ShowDialog() == true ? dialog.Result : cancelResult;
    }

    private static Button? FindDefault(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button { IsDefault: true } button)
            {
                return button;
            }

            if (FindDefault(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
