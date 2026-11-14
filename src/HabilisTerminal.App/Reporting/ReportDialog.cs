using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Reporting;

namespace HabilisTerminal.App.Reporting;

/// <summary>
/// The crash report offered on the next start (read-only, Send / Don't send) and the feedback editor (Enter is a new
/// line, Ctrl+Enter or Send sends, Esc asks before dropping text). Sending happens in the dialog: the created issue's
/// link is shown on success, the text stays on failure with the browser page as a way out.
/// </summary>
internal sealed class ReportDialog : Widgets.ModalWindow
{
    private readonly TextBox? _title;
    private readonly TextBox _text;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), IsVisible = false };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
    private readonly Func<Task<SendResult>> _send;
    private readonly Func<IssueDraft> _draft;
    private readonly bool _isFeedback;
    private bool _busy;
    private IssueLink? _sent;

    private ReportDialog(string heading, string intro, bool isFeedback, string text, Func<IssueDraft> draft, Func<Task<SendResult>> send)
    {
        _isFeedback = isFeedback;
        _draft = draft;
        _send = send;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;
        Title = heading;

        var layout = new StackPanel { Width = 640 };
        layout.Children.Add(Label(heading, "Habi.LabelStyle", FontWeight.SemiBold, new Thickness(0, 0, 0, 8)));
        layout.Children.Add(Label(intro, "Habi.DescriptionStyle", FontWeight.Normal, new Thickness(0, 0, 0, 10)));

        if (isFeedback)
        {
            layout.Children.Add(Label("Title (optional; the first line is used when empty)", "Habi.LabelStyle", FontWeight.Normal, new Thickness(0, 0, 0, 4)));
            _title = new TextBox { Margin = new Thickness(0, 0, 0, 10) };
            _title.AddHandler(KeyDownEvent, (_, e) =>
            {
                // Enter in the title goes on to the text instead of sending.
                if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
                {
                    e.Handled = true;
                    _text?.Focus();
                }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            layout.Children.Add(_title);
        }

        _text = new TextBox
        {
            Text = text,
            AcceptsReturn = isFeedback,
            AcceptsTab = false,
            IsReadOnly = !isFeedback,
            TextWrapping = isFeedback ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            Height = isFeedback ? 260 : 340,
        };
        if (!isFeedback)
        {
            _text.FontFamily = new FontFamily(Theming.Fonts.Mono);
            _text.FontSize = 12;
        }

        layout.Children.Add(_text);
        if (isFeedback)
        {
            layout.Children.Add(Label("Enter starts a new line. Ctrl+Enter sends.", "Habi.DescriptionStyle", FontWeight.Normal, new Thickness(0, 4, 0, 0)));
        }

        layout.Children.Add(_status);
        layout.Children.Add(_buttons);

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
            if (e.Source is Border or TextBlock && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };
        Content = frame;
        Widgets.UiZoom.Attach(this);

        AddHandler(Avalonia.Input.InputElement.KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ShowEditing();
        Loaded += (_, _) =>
        {
            if (_isFeedback)
            {
                _text.Focus();
            }
            else
            {
                _buttons.Children.OfType<Button>().FirstOrDefault()?.Focus();
            }
        };
    }

    /// <summary>Offers the newest waiting crash report; whatever the answer, the waiting files are removed afterwards.</summary>
    public static void OfferCrash(Window owner, IReadOnlyList<(string Path, CrashReport Report)> pending)
    {
        var report = pending[0].Report;
        var more = pending.Count > 1 ? $" {pending.Count - 1} older report(s) are dropped." : string.Empty;
        var intro = "Habilis Terminal crashed last time. You can send this report to the public issue tracker on GitHub " +
                    $"(voronkovlabs/habilis-terminal). Your profile folder, user name and computer name are hidden.{more}";
        var dialog = new ReportDialog("Send crash report?", intro, isFeedback: false,
            IssueText.CrashPreview(report, ReportMasking.Current),
            () => IssueText.Crash(report, ReportMasking.Current),
            () => ReportSender.SendCrashAsync(report)) { ModalOwner = owner };
        dialog.ShowModal();
        foreach (var (path, _) in pending)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static void ShowFeedback(Window owner)
    {
        ReportDialog? dialog = null;
        IssueDraft Draft() => IssueText.Feedback(dialog!._title!.Text, dialog._text.Text ?? string.Empty, ReportSender.Version, ReportEnvironment.Windows(), ReportMasking.Current,
            PanelErrors.Section(ReportMasking.Current, "### Recent panel errors"));
        var intro = "Tell us about problems and wishes. It becomes a public issue on GitHub (voronkovlabs/habilis-terminal) " +
                    "with the Habilis Terminal and Windows versions and the last panel errors (F9 > Options > Panel error log); " +
                    "paths, your profile folder, user name and computer name are hidden.";
        dialog = new ReportDialog("Send feedback", intro, isFeedback: true, string.Empty, Draft, () => ReportSender.SendFeedbackAsync(Draft())) { ModalOwner = owner };
        dialog.ShowModal();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            TryCancel();
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            if (_sent is null)
            {
                _ = SendAsync();
            }

            return;
        }

        // Access keys without Alt, as in the other dialogs, unless typing in an editable text box.
        if (Keyboard.FocusedElement is TextBox { IsReadOnly: false } || Keyboard.Modifiers != KeyModifiers.None)
        {
            return;
        }

        var letter = e.Key.ToString();
        foreach (var button in _buttons.Children.OfType<Button>().Where(button => button.IsEnabled))
        {
            var label = (string)button.Content!;
            var marker = label.IndexOf('_', StringComparison.Ordinal);
            if (marker >= 0 && marker + 1 < label.Length && letter.Length == 1 && char.ToUpperInvariant(label[marker + 1]) == letter[0])
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                e.Handled = true;
                return;
            }
        }
    }

    private void TryCancel()
    {
        if (_busy)
        {
            return;
        }

        var hasText = _isFeedback && _sent is null && (!string.IsNullOrWhiteSpace(_text.Text) || !string.IsNullOrWhiteSpace(_title?.Text));
        if (hasText)
        {
            var body = Label("The text you wrote will be lost.", "Habi.LabelStyle", FontWeight.Normal, default);
            var discard = ThemedDialog.Show(this, "Discard feedback?", body,
                [new DialogButton<bool>("_Discard", true), new DialogButton<bool>("_Keep writing", false, IsDefault: true, IsCancel: true)], false);
            if (!discard)
            {
                _text.Focus();
                return;
            }
        }

        Close();
    }

    private async Task SendAsync()
    {
        if (_busy)
        {
            return;
        }

        if (_isFeedback && string.IsNullOrWhiteSpace(_text.Text))
        {
            ShowStatus("Write something first.", error: true);
            _text.Focus();
            return;
        }

        _busy = true;
        foreach (var button in _buttons.Children.OfType<Button>())
        {
            button.IsEnabled = false;
        }

        _text.IsReadOnly = true;

        ShowStatus(ReportSender.HasToken ? "Sending…" : "Opening the new issue page in your browser…", error: false);
        var result = await _send();
        _busy = false;
        _text.IsReadOnly = !_isFeedback;
        if (result.OpenedInBrowser)
        {
            Close();
        }
        else if (result.Link is { } link)
        {
            ShowSent(link);
        }
        else
        {
            ShowStatus($"Could not send: {result.Error?.TrimEnd('.')}. Your text is kept; try again or open the page in the browser.", error: true);
            ShowFailed();
        }
    }

    private void ShowEditing()
    {
        _buttons.Children.Clear();
        AddButton("_Send", () => _ = SendAsync(), isDefault: true);
        AddButton(_isFeedback ? "_Cancel" : "_Don't send", TryCancel);
    }

    private void ShowFailed()
    {
        _buttons.Children.Clear();
        AddButton("_Send again", () => _ = SendAsync(), isDefault: true);
        AddButton("Open in _browser", () =>
        {
            ReportSender.OpenInBrowser(_draft());
            Close();
        });
        AddButton(_isFeedback ? "_Cancel" : "_Don't send", TryCancel);
    }

    private void ShowSent(IssueLink link)
    {
        _sent = link;
        _status.Inlines?.Clear();
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Foreground");
        _status.Text = (link.IsComment ? $"Thank you. This crash was already reported; added to issue #{link.Number}: " : $"Thank you. Created issue #{link.Number}: ") + link.Url;
        _status.IsVisible = true;

        _buttons.Children.Clear();
        AddButton("_Close", Close, isDefault: true);
        AddButton("_Open issue", () => App.OpenInBrowser(link.Url));
        _buttons.Children.OfType<Button>().First().Focus();
    }

    private void ShowStatus(string text, bool error)
    {
        _status.Inlines?.Clear();
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, error ? "Habi.Error" : "Habi.Foreground");
        _status.IsVisible = true;
    }

    private void AddButton(string label, Action click, bool isDefault = false)
    {
        var button = new Button { Content = label, IsDefault = isDefault };
        button.Click += (_, _) => click();
        _buttons.Children.Add(button);
    }

    private static TextBlock Label(string text, string style, FontWeight weight, Thickness margin)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = weight, Margin = margin };
        block.Classes.Add(style == "Habi.DescriptionStyle" ? "description" : "label");
        return block;
    }
}
