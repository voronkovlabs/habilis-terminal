using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Theming;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.App.Viewing;

/// <summary>
/// F3 (SPEC §9): full-window viewer with Far keys — F2 wrap, F4 hex, F7 / Shift+F7 / Alt+F7 search, F8 ANSI ↔ OEM,
/// Shift+F8 encoding, Alt+F8 go to line / offset / percent, F6 editor, Esc / F3 / F10 close. Images are shown
/// with the WPF decoders.
/// </summary>
public sealed class ViewerPage : DockPanel, IDisposable
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
    private string _lastSearch = string.Empty;

    public ViewerPage(string path, bool wrap = false)
    {
        _view.Wrap = wrap;
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
    }

    /// <summary>Esc / F3 / F10.</summary>
    public event Action? CloseRequested;

    /// <summary>F6: switch to the editor (stage 9).</summary>
    public event Action<string>? EditRequested;

    public string FilePath { get; }

    public void Dispose() => _document?.Dispose();

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
                _document.Encoding = _document.Encoding.Name == TextEncoding.Oem.Name ? TextEncoding.Ansi : TextEncoding.Oem;
                _view.Refresh();
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
        Focus();
        if (result?.Item?.Tag is TextEncoding encoding)
        {
            _document!.Encoding = encoding;
            _view.Refresh();
        }
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
        _info.Text = $"{PanelColumns.FormatCompactSize(_document.Length, culture)}  ·  {_document.Encoding.Name}  ·  {(_view.Hex ? "Hex" : _view.Wrap ? "Wrap" : "Text")}  ·  {_view.Percent:0}%";
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
