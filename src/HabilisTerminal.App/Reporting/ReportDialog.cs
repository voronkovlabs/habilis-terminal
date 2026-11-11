using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Reporting;

namespace HabilisTerminal.App.Reporting;

/// <summary>
/// The crash report offered on the next start (read-only, Send / Don't send) and the feedback editor (Enter is a new
/// line, Ctrl+Enter or Send sends, Esc asks before dropping text). Sending happens in the dialog: the created issue's
/// link is shown on success, the text stays on failure with the browser page as a way out.
/// </summary>
internal sealed class ReportDialog : Window
{
    private readonly TextBox? _title;
    private readonly TextBox _text;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
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
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;
        Title = heading;

        var layout = new StackPanel { Width = 640 };
        layout.Children.Add(Label(heading, "Habi.LabelStyle", FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));
        layout.Children.Add(Label(intro, "Habi.DescriptionStyle", FontWeights.Normal, new Thickness(0, 0, 0, 10)));

        if (isFeedback)
        {
            layout.Children.Add(Label("Title (optional; the first line is used when empty)", "Habi.LabelStyle", FontWeights.Normal, new Thickness(0, 0, 0, 4)));
            _title = new TextBox { Margin = new Thickness(0, 0, 0, 10) };
            _title.PreviewKeyDown += (_, e) =>
            {
                // Enter in the title goes on to the text instead of sending.
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
                {
                    e.Handled = true;
                    _text?.Focus();
                }
            };
            layout.Children.Add(_title);
        }

        _text = new TextBox
        {
            Text = text,
            AcceptsReturn = isFeedback,
            AcceptsTab = false,
            IsReadOnly = !isFeedback,
            TextWrapping = isFeedback ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = isFeedback ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            Height = isFeedback ? 260 : 340,
        };
        if (!isFeedback)
        {
            _text.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            _text.FontSize = 12;
        }

        layout.Children.Add(_text);
        if (isFeedback)
        {
            layout.Children.Add(Label("Enter starts a new line. Ctrl+Enter sends.", "Habi.DescriptionStyle", FontWeights.Normal, new Thickness(0, 4, 0, 0)));
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
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black },
        };
        frame.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        frame.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");
        frame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is Border or TextBlock && e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
        Content = frame;
        Widgets.UiZoom.Attach(this);

        PreviewKeyDown += OnPreviewKeyDown;
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
            () => ReportSender.SendCrashAsync(report)) { Owner = owner };
        dialog.ShowDialog();
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
        IssueDraft Draft() => IssueText.Feedback(dialog!._title!.Text, dialog._text.Text, ReportSender.Version, ReportEnvironment.Windows(), ReportMasking.Current);
        var intro = "Tell us about problems and wishes. It becomes a public issue on GitHub (voronkovlabs/habilis-terminal) " +
                    "with the Habilis Terminal and Windows versions; your profile folder, user name and computer name are hidden.";
        dialog = new ReportDialog("Send feedback", intro, isFeedback: true, string.Empty, Draft, () => ReportSender.SendFeedbackAsync(Draft())) { Owner = owner };
        dialog.ShowDialog();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            TryCancel();
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (_sent is null)
            {
                _ = SendAsync();
            }

            return;
        }

        // Access keys without Alt, as in the other dialogs, unless typing in an editable text box.
        if (Keyboard.FocusedElement is TextBox { IsReadOnly: false } || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        var letter = e.Key.ToString();
        foreach (var button in _buttons.Children.OfType<Button>().Where(button => button.IsEnabled))
        {
            var label = (string)button.Content;
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

        var hasText = _isFeedback && _sent is null && (_text.Text.Trim().Length > 0 || _title?.Text.Trim().Length > 0);
        if (hasText)
        {
            var body = Label("The text you wrote will be lost.", "Habi.LabelStyle", FontWeights.Normal, default);
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

        if (_isFeedback && _text.Text.Trim().Length == 0)
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
        _status.Inlines.Clear();
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Foreground");
        _status.Inlines.Add(new Run(link.IsComment ? $"Thank you. This crash was already reported; added to issue #{link.Number}: " : $"Thank you. Created issue #{link.Number}: "));
        var hyperlink = new Hyperlink(new Run(link.Url)) { NavigateUri = new Uri(link.Url) };
        hyperlink.SetResourceReference(TextElement.ForegroundProperty, "Habi.Accent");
        hyperlink.RequestNavigate += (_, e) => App.OpenInBrowser(e.Uri.AbsoluteUri);
        _status.Inlines.Add(hyperlink);
        _status.Visibility = Visibility.Visible;

        _buttons.Children.Clear();
        AddButton("_Close", Close, isDefault: true);
        AddButton("_Open issue", () => App.OpenInBrowser(link.Url));
        _buttons.Children.OfType<Button>().First().Focus();
    }

    private void ShowStatus(string text, bool error)
    {
        _status.Inlines.Clear();
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, error ? "Habi.Error" : "Habi.Foreground");
        _status.Visibility = Visibility.Visible;
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
        block.SetResourceReference(StyleProperty, style);
        return block;
    }
}
