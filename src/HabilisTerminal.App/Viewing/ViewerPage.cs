using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HabilisTerminal.App.Editing;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Theming;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Editing;
using HabilisTerminal.Core.Settings;
using HabilisTerminal.Core.Viewing;
using HabilisTerminal.Themes;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace HabilisTerminal.App.Viewing;

/// <summary>
/// F3 (SPEC §9): full-window viewer with Far keys — F2 wrap, F4 hex, F7 / Shift+F7 / Alt+F7 search, F8 ANSI ↔ OEM,
/// Shift+F8 encoding, Alt+F8 go to line / offset / percent, F6 editor, Esc / F3 / F10 close. Images are shown
/// with the WPF decoders. Text files up to <c>viewer.textModeMaxMB</c> show in a read-only editor (issue #56): syntax
/// colours, mouse selection, Ctrl+C; F4 still switches to hex, and larger files use the paged <see cref="ViewerView"/>.
/// </summary>
public sealed class ViewerPage : DockPanel, IDisposable, IPage
{
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff",
    };

    private readonly ViewerView _view = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _info = new();
    private readonly TextBlock _hint = new();
    private readonly ViewerDocument? _document;
    private TextEditor? _text;
    private TextFile? _textFile;
    private bool _closed;
    private string _lastSearch = string.Empty;

    public ViewerPage(string path, AppSettings settings, ColorTheme theme)
    {
        _view.Wrap = settings.ViewerWrap;
        FilePath = path;
        Focusable = true;
        FocusVisualStyle = null;
        Background = PanelPalette.Background;
        LastChildFill = true;

        _title.FontWeight = FontWeights.SemiBold;
        foreach (var text in new[] { _title, _info, _hint })
        {
            text.FontFamily = new FontFamily("Segoe UI");
            text.FontSize = 12;
            text.Foreground = text == _title ? PanelPalette.Foreground : PanelPalette.DescriptionForeground;
            text.Margin = new Thickness(0, 0, 16, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
        }

        _hint.Text = "F2 wrap · F4 hex · F7 find · Shift+F8 encoding · Alt+F8 go to · F6 edit · Esc close";
        var header = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
        DockPanel.SetDock(_hint, Dock.Right);
        header.Children.Add(_hint);
        header.Children.Add(_title);
        header.Children.Add(_info);
        var headerBorder = new Border { Child = header, BorderBrush = PanelPalette.Border, BorderThickness = new Thickness(0, 0, 0, 1), Background = PanelPalette.HeaderBackground };
        DockPanel.SetDock(headerBorder, Dock.Top);
        Children.Add(headerBorder);

        _title.Text = path;
        if (ImageExtensions.Contains(Path.GetExtension(path)))
        {
            Children.Add(CreateImage(path));
            return;
        }

        try
        {
            _document = ViewerDocument.Open(path);
            _view.Document = _document;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _info.Text = ex.Message;
        }

        _view.PositionChanged += UpdateInfo;
        Children.Add(_view);
        UpdateInfo();
        if (_document is not null && FitsTextMode(path, settings.ViewerTextModeMaxMB))
        {
            // The paged view shows at once; the text mode is built in the background and takes over when ready, so a
            // large file never freezes the window (issue #76).
            _ = LoadTextModeAsync(path, settings, theme);
        }
    }

    /// <summary>Esc / F3 / F10.</summary>
    public event Action? CloseRequested;

    /// <summary>F6: switch to the editor (stage 9).</summary>
    public event Action<string>? EditRequested;

    public string FilePath { get; }

    /// <summary>The read-only editor is showing (not hex, not the paged view).</summary>
    private bool ShowingText => _text is not null && _text.Parent == this;

    public void Dispose()
    {
        _closed = true;
        _document?.Dispose();
    }

    public void FocusContent()
    {
        if (ShowingText)
        {
            _text!.Focus();
        }
        else
        {
            Focus();
        }
    }

    /// <summary>The text mode's colours follow the colour theme; the paged view reads <see cref="PanelPalette"/>.</summary>
    public void ApplyTheme(ColorTheme theme)
    {
        if (_text is not null)
        {
            TextEditorTheme.Apply(_text, theme);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        e.Handled = true;
        switch (key)
        {
            case Key.Escape or Key.F3 or Key.F10 when modifiers == ModifierKeys.None:
                CloseRequested?.Invoke();
                return;
            case Key.F6 when modifiers == ModifierKeys.None:
                EditRequested?.Invoke(FilePath);
                return;
        }

        if (_document is null)
        {
            e.Handled = false;
            return;
        }

        if (ShowingText)
        {
            e.Handled = HandleTextModeKey(key, modifiers);
            return;
        }

        if (key == Key.F4 && modifiers == ModifierKeys.None && _text is not null)
        {
            ShowText();
            return;
        }

        switch (key)
        {
            case Key.Down when modifiers == ModifierKeys.None:
                _view.ScrollLines(1);
                break;
            case Key.Up when modifiers == ModifierKeys.None:
                _view.ScrollLines(-1);
                break;
            case Key.PageDown:
                _view.ScrollPages(1);
                break;
            case Key.PageUp:
                _view.ScrollPages(-1);
                break;
            case Key.Home when modifiers == ModifierKeys.Control:
                _view.GoToStart();
                break;
            case Key.End when modifiers == ModifierKeys.Control:
                _view.GoToEnd();
                break;
            case Key.Home:
                _view.ScrollHorizontally(-_view.LeftColumn);
                break;
            case Key.Right:
                _view.ScrollHorizontally(modifiers == ModifierKeys.Control ? 20 : 1);
                break;
            case Key.Left:
                _view.ScrollHorizontally(modifiers == ModifierKeys.Control ? -20 : -1);
                break;
            case Key.F2 when modifiers == ModifierKeys.None:
                _view.Wrap = !_view.Wrap;
                _view.Refresh();
                if (_text is not null)
                {
                    _text.WordWrap = _view.Wrap;
                }

                break;
            case Key.F4 when modifiers == ModifierKeys.None:
                _view.Hex = !_view.Hex;
                _view.Refresh();
                break;
            case Key.F7 when modifiers == ModifierKeys.None:
                _ = SearchAsync(prompt: true, forward: true);
                break;
            case Key.F7 when modifiers == ModifierKeys.Shift:
                _ = SearchAsync(prompt: false, forward: true);
                break;
            case Key.F7 when modifiers == ModifierKeys.Alt:
                _ = SearchAsync(prompt: false, forward: false);
                break;
            case Key.F8 when modifiers == ModifierKeys.None:
                // Far: F8 toggles between the ANSI and OEM code pages.
                SetEncoding(_document.Encoding.Name == TextEncoding.Oem.Name ? TextEncoding.Ansi : TextEncoding.Oem);
                break;
            case Key.F8 when modifiers == ModifierKeys.Shift:
                _ = ChooseEncodingAsync();
                break;
            case Key.F8 when modifiers == ModifierKeys.Alt:
                _ = GoToAsync();
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private async Task SearchAsync(bool prompt, bool forward)
    {
        if (prompt || _lastSearch.Length == 0)
        {
            var result = await QuickPick.ShowAsync(this, [], new QuickPickOptions { Title = "Find", Placeholder = "Text to find (case-insensitive)", InitialText = _lastSearch, AllowFreeText = true });
            Focus();
            if (result is null || result.Text.Length == 0)
            {
                return;
            }

            _lastSearch = result.Text;
        }

        var document = _document!;
        var start = _view.Highlight is { } hit ? (forward ? hit.Offset + 1 : hit.Offset) : (forward ? _view.TopOffset : _view.TopOffset);
        var text = _lastSearch;
        _info.Text = "Searching…";
        var found = await Task.Run(() => document.Find(text, start, forward, matchCase: false));
        if (found is { } offset)
        {
            _view.Highlight = (offset, text.Length);
            _view.Reveal(offset);
        }
        else
        {
            UpdateInfo();
            _info.Text += $"  ·  '{text}' not found";
        }
    }

    private async Task ChooseEncodingAsync()
    {
        var items = TextEncoding.Choices.Select(e => new QuickPickItem { Label = e.Name, Tag = e, IsCurrent = e.Name == _document!.Encoding.Name }).ToList();
        var result = await QuickPick.ShowAsync(this, items, new QuickPickOptions { Title = "Encoding" });
        FocusContent();
        if (result?.Item?.Tag is TextEncoding encoding)
        {
            SetEncoding(encoding);
        }
    }

    private void SetEncoding(TextEncoding encoding)
    {
        _document!.Encoding = encoding;
        _view.Refresh();
        if (_text is not null && _textFile is not null)
        {
            try
            {
                _textFile = _textFile.Reinterpret(encoding);
                var caret = _text.CaretOffset;
                _text.Text = _textFile.Text;
                _text.CaretOffset = Math.Min(caret, _text.Document.TextLength);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _info.Text = ex.Message;
                return;
            }
        }

        UpdateInfo();
    }

    /// <summary>Alt+F8: a line number, <c>0x…</c> for a byte offset, or <c>NN%</c>.</summary>
    private async Task GoToAsync()
    {
        var result = await QuickPick.ShowAsync(this, [], new QuickPickOptions { Title = "Go to", Placeholder = "Line number, 0x offset or percent (50%)", AllowFreeText = true });
        Focus();
        var text = result?.Text.Trim() ?? string.Empty;
        var document = _document!;
        long? offset = null;
        if (text.EndsWith('%') && double.TryParse(text.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            offset = (long)(document.Length * Math.Clamp(percent, 0, 100) / 100);
        }
        else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            offset = hex;
        }
        else if (long.TryParse(text, out var line) && line > 0)
        {
            offset = await Task.Run(() => document.OffsetOfLine(line));
        }

        if (offset is { } target)
        {
            _view.Reveal(target);
        }
    }

    private void UpdateInfo()
    {
        if (_document is null)
        {
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        if (ShowingText)
        {
            var caret = _text!.TextArea.Caret;
            _info.Text = $"{PanelColumns.FormatCompactSize(_document.Length, culture)}  ·  {_textFile!.Encoding.Name}  ·  {(_text.WordWrap ? "Wrap" : "Text")}  ·  Ln {caret.Line} of {_text.LineCount}, Col {caret.Column}";
            return;
        }

        _info.Text = $"{PanelColumns.FormatCompactSize(_document.Length, culture)}  ·  {_document.Encoding.Name}  ·  {(_view.Hex ? "Hex" : _view.Wrap ? "Wrap" : "Text")}  ·  {_view.Percent:0}%";
    }

    /// <summary>A text file small enough to load whole into the read-only editor.</summary>
    internal static bool FitsTextMode(string path, int maxMB)
    {
        try
        {
            if (maxMB <= 0 || new FileInfo(path).Length > maxMB * 1024L * 1024)
            {
                return false;
            }

            var sample = new byte[64 * 1024];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = stream.Read(sample, 0, sample.Length);
            return TextEncoding.LooksLikeText(sample.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads and decodes the file and builds the editor's document off the UI thread, then shows the text mode in place
    /// of the paged view at about the same place; not when the page closed or the user switched to hex meanwhile.
    /// </summary>
    private async Task LoadTextModeAsync(string path, AppSettings settings, ColorTheme theme)
    {
        var ui = Dispatcher.Thread;
        (TextFile File, ICSharpCode.AvalonEdit.Document.TextDocument Document)? result;
        try
        {
            result = await Task.Run<(TextFile, ICSharpCode.AvalonEdit.Document.TextDocument)?>(() =>
            {
                if (Core.Editing.LongLines.HasHugeLine(path))
                {
                    return null; // the editor control would freeze the window (issue #163)
                }

                var file = TextFile.Load(path);
                var document = new ICSharpCode.AvalonEdit.Document.TextDocument(file.Text);
                document.SetOwnerThread(ui);
                return (file, document);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            return; // the paged view stays
        }

        if (result is not { } loaded || _closed || _view.Hex || !ReferenceEquals(_view.Parent, this))
        {
            return;
        }

        _textFile = loaded.File;
        _text = CreateTextEditor(loaded.File, loaded.Document, settings, theme);
        var share = _document!.Length > 0 ? (double)_view.TopOffset / _document.Length : 0;
        var hadFocus = IsKeyboardFocusWithin;
        Children.Remove(_view);
        Children.Add(_text);
        if (share > 0)
        {
            var line = Math.Clamp((int)Math.Round(share * _text.LineCount), 1, _text.LineCount);
            _text.TextArea.Caret.Line = line;
            _text.ScrollTo(line, 0);
        }

        UpdateInfo();
        if (hadFocus)
        {
            _ = Dispatcher.BeginInvoke(() => _text.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private TextEditor CreateTextEditor(TextFile file, ICSharpCode.AvalonEdit.Document.TextDocument document, AppSettings settings, ColorTheme theme)
    {
        var editor = new TextEditor
        {
            IsReadOnly = true,
            FontFamily = new FontFamily(settings.ViewerFontFamily + ", Consolas"),
            FontSize = settings.ViewerFontSize,
            ShowLineNumbers = true,
            WordWrap = settings.ViewerWrap,
            Document = document,
            SyntaxHighlighting = settings.EditorSyntaxHighlighting && !Core.Editing.LongLines.TooLongToColor(file.Text) ? HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(file.Path)) : null,
        };
        editor.Options.HighlightCurrentLine = true;
        editor.Options.IndentationSize = settings.EditorTabSize;
        editor.TextArea.Caret.PositionChanged += (_, _) => UpdateInfo();
        TextEditorTheme.Apply(editor, theme);
        return editor;
    }

    /// <summary>Viewer keys in the text mode; everything else (moving, selecting, Ctrl+C) goes to the editor.</summary>
    private bool HandleTextModeKey(Key key, ModifierKeys modifiers)
    {
        switch (key)
        {
            case Key.F2 when modifiers == ModifierKeys.None:
                _text!.WordWrap = !_text.WordWrap;
                _view.Wrap = _text.WordWrap;
                UpdateInfo();
                return true;
            case Key.F4 when modifiers == ModifierKeys.None:
                ShowHex();
                return true;
            case Key.F7 when modifiers == ModifierKeys.None:
                _ = FindInTextAsync(prompt: true, forward: true);
                return true;
            case Key.F7 when modifiers == ModifierKeys.Shift:
                _ = FindInTextAsync(prompt: false, forward: true);
                return true;
            case Key.F7 when modifiers == ModifierKeys.Alt:
                _ = FindInTextAsync(prompt: false, forward: false);
                return true;
            case Key.F8 when modifiers == ModifierKeys.None:
                SetEncoding(_textFile!.Encoding.Name == TextEncoding.Oem.Name ? TextEncoding.Ansi : TextEncoding.Oem);
                return true;
            case Key.F8 when modifiers == ModifierKeys.Shift:
                _ = ChooseEncodingAsync();
                return true;
            case Key.F8 when modifiers == ModifierKeys.Alt:
                _ = GoToInTextAsync();
                return true;
            default:
                return false;
        }
    }

    /// <summary>F4 in the text mode: the paged hex view, at <paramref name="offset"/> or else at the caret's line.</summary>
    private void ShowHex(long? offset = null)
    {
        var line = _text!.TextArea.Caret.Line;
        Children.Remove(_text);
        Children.Add(_view);
        _view.Hex = true;
        _view.Refresh();
        if (offset is { } target)
        {
            _view.Reveal(target);
        }
        else
        {
            _ = RevealLineAsync(line);
        }

        UpdateInfo();
        Focus();
    }

    private void ShowText()
    {
        _view.Hex = false;
        Children.Remove(_view);
        Children.Add(_text!);
        UpdateInfo();
        Dispatcher.BeginInvoke(() => _text!.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private async Task RevealLineAsync(int line)
    {
        var document = _document!;
        var offset = await Task.Run(() => document.OffsetOfLine(line));
        _view.Reveal(offset);
    }

    private async Task FindInTextAsync(bool prompt, bool forward)
    {
        if (prompt || _lastSearch.Length == 0)
        {
            var result = await QuickPick.ShowAsync(this, [], new QuickPickOptions { Title = "Find", Placeholder = "Text to find (case-insensitive)", InitialText = _lastSearch, AllowFreeText = true });
            _text!.Focus();
            if (result is null || result.Text.Length == 0)
            {
                return;
            }

            _lastSearch = result.Text;
        }

        var text = _text!.Text;
        var comparison = StringComparison.CurrentCultureIgnoreCase;
        int index;
        if (forward)
        {
            var start = _text.SelectionLength > 0 ? _text.SelectionStart + 1 : _text.CaretOffset;
            index = start < text.Length ? text.IndexOf(_lastSearch, start, comparison) : -1;
        }
        else
        {
            var start = (_text.SelectionLength > 0 ? _text.SelectionStart : _text.CaretOffset) - 1;
            index = start >= 0 ? text.LastIndexOf(_lastSearch, start, comparison) : -1;
        }

        if (index < 0)
        {
            UpdateInfo();
            _info.Text += $"  ·  '{_lastSearch}' not found";
            return;
        }

        _text.Select(index, _lastSearch.Length);
        _text.ScrollTo(_text.Document.GetLineByOffset(index).LineNumber, 0);
    }

    /// <summary>Alt+F8 in the text mode: a line or a percent here; a <c>0x</c> byte offset opens the hex view there.</summary>
    private async Task GoToInTextAsync()
    {
        var result = await QuickPick.ShowAsync(this, [], new QuickPickOptions { Title = "Go to", Placeholder = "Line number, 0x offset or percent (50%)", AllowFreeText = true });
        _text!.Focus();
        var text = result?.Text.Trim() ?? string.Empty;
        int? line = null;
        if (text.EndsWith('%') && double.TryParse(text.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            line = Math.Max(1, (int)Math.Round(_text.LineCount * Math.Clamp(percent, 0, 100) / 100));
        }
        else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            ShowHex(hex);
            return;
        }
        else if (int.TryParse(text, out var number) && number > 0)
        {
            line = Math.Min(number, _text.LineCount);
        }

        if (line is { } target)
        {
            _text.TextArea.Caret.Line = target;
            _text.TextArea.Caret.Column = 1;
            _text.ScrollTo(target, 0);
        }
    }

    private UIElement CreateImage(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            _info.Text = $"{image.PixelWidth} × {image.PixelHeight}  ·  {PanelColumns.FormatCompactSize(new FileInfo(path).Length, CultureInfo.CurrentCulture)}";
            return new Image { Source = image, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(12) };
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            _info.Text = $"Cannot show the image: {ex.Message}";
            return new Grid();
        }
    }
}
