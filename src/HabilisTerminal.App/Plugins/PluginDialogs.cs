using System.Windows;
using System.Windows.Controls;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Plugins;

namespace HabilisTerminal.App.Plugins;

/// <summary>
/// Plugin dialogs drawn with the host's themed dialog and quick pick (docs/plugins.md §4). Callable from any thread:
/// everything runs on the UI thread.
/// </summary>
internal sealed class PluginDialogs(Window owner, Func<FrameworkElement> pickTarget) : IPluginDialogs
{
    public Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Information) =>
        OnUi(() =>
        {
            var body = new StackPanel();
            body.Children.Add(Label(message, kind == MessageKind.Error ? "Habi.Error" : null));
            ThemedDialog.Show(owner, title, body, [new DialogButton<bool>("OK", true, IsDefault: true, IsCancel: true)], false);
            return true;
        });

    public Task<bool> ConfirmAsync(string title, string message, IReadOnlyList<string>? items = null, string confirmText = "OK", bool destructive = false) =>
        OnUi(() =>
        {
            var body = new StackPanel();
            body.Children.Add(Label(message));
            if (items is { Count: > 0 })
            {
                // The items affected, as in Far's delete confirmation; long lists scroll.
                var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
                foreach (var item in items)
                {
                    list.Children.Add(Label(item, null, bold: true));
                }

                body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            }

            var cancel = new DialogButton<bool>("Cancel", false, IsDefault: destructive, IsCancel: true);
            var confirm = new DialogButton<bool>(confirmText, true, IsDefault: !destructive);
            return ThemedDialog.Show(owner, title, body, [confirm, cancel], false);
        });

    public Task<string?> InputAsync(string title, string prompt, string? initialValue = null) =>
        OnUi(() =>
        {
            var box = new TextBox { Text = initialValue ?? string.Empty };
            var body = new StackPanel();
            body.Children.Add(Label(prompt, bottom: 6));
            body.Children.Add(box);
            var accepted = ThemedDialog.Show(owner, title, body,
                [new DialogButton<bool>("OK", true, IsDefault: true), new DialogButton<bool>("Cancel", false, IsCancel: true)], false, box);
            return accepted ? box.Text : null;
        });

    public async Task<string?> ChooseAsync(string title, IReadOnlyList<string> choices, string? current = null)
    {
        var dispatcher = owner.Dispatcher;
        var task = await dispatcher.InvokeAsync(() =>
        {
            var items = choices.Select(c => new QuickPickItem { Label = c, Tag = c, IsCurrent = c == current }).ToList();
            return QuickPick.ShowAsync(pickTarget(), items, new QuickPickOptions { Title = title });
        });
        var result = await task;
        return result?.Item?.Tag as string;
    }

    public Task<bool> EditAsync(PluginForm form) =>
        OnUi(() =>
        {
            while (true)
            {
                var (body, apply, first) = BuildForm(form);
                var error = new TextBlock { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
                error.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Error");
                body.Children.Add(error);
                var scroll = new ScrollViewer { Content = body, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                if (!ThemedDialog.Show(owner, form.Title, scroll,
                    [new DialogButton<bool>("Save", true, IsDefault: true), new DialogButton<bool>("Cancel", false, IsCancel: true)], false, first))
                {
                    return false;
                }

                // Values go into the fields only when the form validates; otherwise the dialog shows again.
                var snapshot = Snapshot(form);
                apply();
                if (form.Validate?.Invoke(form) is not { } message)
                {
                    return true;
                }

                Restore(form, snapshot);
                ThemedDialog.Show(owner, form.Title, LabelPanel(message), [new DialogButton<bool>("OK", true, IsDefault: true, IsCancel: true)], false);
            }
        });

    private static (StackPanel Body, Action Apply, UIElement? First) BuildForm(PluginForm form)
    {
        var body = new StackPanel { MinWidth = 460 };
        var appliers = new List<Action>();
        UIElement? first = null;
        foreach (var field in form.Fields)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            FrameworkElement editor;
            switch (field)
            {
                case TextField text:
                    var textBox = new TextBox { Text = text.Value };
                    appliers.Add(() => text.Value = textBox.Text.Trim());
                    editor = textBox;
                    break;
                case NumberField number:
                    var numberBox = new TextBox { Text = number.Value.ToString(System.Globalization.CultureInfo.CurrentCulture), Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
                    appliers.Add(() =>
                    {
                        if (int.TryParse(numberBox.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out var value))
                        {
                            number.Value = Math.Clamp(value, number.Minimum, number.Maximum);
                        }
                    });
                    editor = numberBox;
                    break;
                case ToggleField toggle:
                    var check = new CheckBox { Content = toggle.Label, IsChecked = toggle.Value };
                    appliers.Add(() => toggle.Value = check.IsChecked == true);
                    editor = check;
                    break;
                case ChoiceField choice:
                    var combo = new ComboBox { ItemsSource = choice.Choices, IsEditable = choice.Editable, Text = choice.Value, SelectedItem = choice.Choices.Contains(choice.Value) ? choice.Value : null };
                    appliers.Add(() => choice.Value = (choice.Editable ? combo.Text : combo.SelectedItem as string ?? choice.Value).Trim());
                    editor = combo;
                    break;
                case ListField list:
                    var lines = new TextBox { Text = string.Join(Environment.NewLine, list.Values), AcceptsReturn = true, MinHeight = 60, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                    appliers.Add(() => list.Values = lines.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    editor = lines;
                    break;
                default:
                    continue;
            }

            if (field is not ToggleField)
            {
                row.Children.Add(Label(field.Label, bottom: 4));
            }

            row.Children.Add(editor);
            if (field.Description is { } description)
            {
                var hint = Label(description, "Habi.Description", top: 3);
                hint.FontSize = 11;
                row.Children.Add(hint);
            }

            first ??= editor;
            body.Children.Add(row);
        }

        return (body, () => appliers.ForEach(a => a()), first);
    }

    private static Dictionary<FormField, object> Snapshot(PluginForm form) =>
        form.Fields.ToDictionary(f => f, f => f switch
        {
            TextField t => (object)t.Value,
            NumberField n => n.Value,
            ToggleField b => b.Value,
            ChoiceField c => c.Value,
            ListField l => l.Values,
            _ => 0,
        });

    private static void Restore(PluginForm form, Dictionary<FormField, object> snapshot)
    {
        foreach (var field in form.Fields)
        {
            switch (field)
            {
                case TextField t: t.Value = (string)snapshot[field]; break;
                case NumberField n: n.Value = (int)snapshot[field]; break;
                case ToggleField b: b.Value = (bool)snapshot[field]; break;
                case ChoiceField c: c.Value = (string)snapshot[field]; break;
                case ListField l: l.Values = (IReadOnlyList<string>)snapshot[field]; break;
            }
        }
    }

    private static StackPanel LabelPanel(string text)
    {
        var panel = new StackPanel();
        panel.Children.Add(Label(text));
        return panel;
    }

    private static TextBlock Label(string text, string? brushKey = null, bool bold = false, double top = 0, double bottom = 0)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, bottom) };
        block.SetResourceReference(FrameworkElement.StyleProperty, "Habi.LabelStyle");
        if (brushKey is not null)
        {
            block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        }

        if (bold)
        {
            block.FontWeight = FontWeights.SemiBold;
        }

        return block;
    }

    private Task<T> OnUi<T>(Func<T> work) => owner.Dispatcher.InvokeAsync(work).Task;
}
