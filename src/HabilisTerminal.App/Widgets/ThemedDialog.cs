using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using HabilisTerminal.App.Theming;

namespace HabilisTerminal.App.Widgets;

/// <param name="Label">Button text; <c>_</c> marks the access key (Alt+letter or the letter alone).</param>
public sealed record DialogButton<T>(string Label, T Result, bool IsDefault = false, bool IsCancel = false);

/// <summary>
/// A modal dialog in the style of VS Code dialogs: borderless, rounded, themed from <c>Habi.*</c> resources,
/// centered on its owner. Enter presses the default button, Esc the cancel button, access keys work without Alt.
/// </summary>
internal sealed class DialogWindow<T> : ModalWindow
{
    private T _result;

    internal DialogWindow(string title, Control body, IReadOnlyList<DialogButton<T>> buttons, T cancelResult)
    {
        _result = cancelResult;
        Chromeless(this);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");

        var titleText = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        titleText.Classes.Add("label");

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

        var frame = Frame(layout);
        Content = frame;
        UiZoom.Attach(this);

        // Access keys without Alt, as in Far dialogs (only when focus is not in a text box).
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
                return;
            }

            if (FocusManager?.GetFocusedElement() is TextBox || (e.KeyModifiers != KeyModifiers.None && e.KeyModifiers != KeyModifiers.Alt))
            {
                return;
            }

            var letter = e.Key.ToString();
            foreach (var button in buttonRow.Children.OfType<Button>())
            {
                var label = (string)button.Content!;
                var marker = label.IndexOf('_', StringComparison.Ordinal);
                if (marker >= 0 && marker + 1 < label.Length && letter.Length == 1 &&
                    char.ToUpperInvariant(label[marker + 1]) == letter[0])
                {
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    e.Handled = true;
                    return;
                }
            }
        }, RoutingStrategies.Tunnel);

        // Dragging by the frame moves the dialog.
        frame.PointerPressed += (_, e) =>
        {
            if (e.Source is Border or TextBlock && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };
    }

    internal T Result => _result;

    /// <summary>A borderless, transparent window: the rounded frame with its shadow is all that shows.</summary>
    internal static void Chromeless(Window window)
    {
        window.WindowDecorations = WindowDecorations.None;
        window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        window.Background = Brushes.Transparent;
        window.CanResize = false;
        window.SizeToContent = SizeToContent.WidthAndHeight;
        window.ShowInTaskbar = false;
        window.UseLayoutRounding = true;
    }

    /// <summary>The rounded, shadowed frame of our dialogs.</summary>
    internal static Border Frame(Control content)
    {
        var frame = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 16, 20, 16),
            Margin = new Thickness(16),
            Child = content,
            BoxShadow = BoxShadows.Parse("0 4 18 0 #73000000"),
        };
        frame.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        frame.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");
        return frame;
    }
}

public static class ThemedDialog
{
    /// <summary>Shows a dialog modally; returns the chosen button's result or <paramref name="cancelResult"/>.</summary>
    /// <param name="owner">The window to centre on; null before the main window exists (centred on the screen, in the taskbar).</param>
    public static T Show<T>(Window? owner, string title, Control body, IReadOnlyList<DialogButton<T>> buttons, T cancelResult, Control? focus = null)
    {
        var dialog = new DialogWindow<T>(title, body, buttons, cancelResult) { ModalOwner = owner };
        if (owner is null)
        {
            dialog.ShowInTaskbar = true;
            dialog.Title = title;
        }

        dialog.Opened += (_, _) =>
        {
            var target = focus ?? (Control?)FindDefault(dialog) ?? dialog;
            target.Focus();
            if (target is TextBox textBox)
            {
                textBox.SelectAll();
            }
        };
        return dialog.ShowModal() == true ? dialog.Result : cancelResult;
    }

    private static Button? FindDefault(Visual root) => root.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsDefault);
}
