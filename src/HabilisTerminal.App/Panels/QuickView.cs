using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HabilisTerminal.App.Editing;
using HabilisTerminal.App.Theming;
using HabilisTerminal.App.Viewing;
using HabilisTerminal.Core.Editing;
using HabilisTerminal.Core.Settings;
using HabilisTerminal.Core.Viewing;
using HabilisTerminal.Themes;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace HabilisTerminal.App.Panels;

/// <summary>
/// Ctrl+Q (SPEC §9): the current file of the other panel as text or image, folders as a summary; for a plugin panel
/// the text its provider gives (<c>IQuickViewEntry</c>). Text files follow the F3 rule (issue #74): up to
/// <c>viewer.textModeMaxMB</c> in a read-only editor with syntax colours, larger ones in the paged view. A PDF shows as
/// page images (issue #110); the keyboard stays in the panels, so the mouse wheel turns its pages.
/// </summary>
public sealed class QuickView : Grid, IDisposable
{
    private readonly ViewerView _text = new();
    private readonly Image _image = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(8) };
    private readonly TextBlock _message = new() { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Segoe UI"), FontSize = 13 };
    private readonly TextBlock _plain = new() { Margin = new Thickness(10, 8, 10, 8), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13 };
    private readonly ScrollViewer _plainScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
    private readonly TextEditor _editor = new()
    {
        IsReadOnly = true,
        ShowLineNumbers = true,
        Focusable = false,
        Visibility = Visibility.Collapsed,
    };

    private readonly TextBlock _pdfPage = new()
    {
        Margin = new Thickness(12, 6, 12, 6),
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Visibility = Visibility.Collapsed,
    };

    private ViewerDocument? _document;
    private string? _path;
    private PdfPreview? _pdf;
    private int _pdfIndex;
    private int _pdfRequest;

    /// <summary>The settings and colour theme of the text mode, set by the window (like <see cref="ViewerView.SetFont"/>).</summary>
    public static AppSettings? Settings { get; set; }

    public static ColorTheme? Theme { get; set; }

    public QuickView()
    {
        Children.Add(_text);
        _editor.TextArea.Focusable = false; // the keyboard stays in the panels
        Children.Add(_editor);
        Children.Add(_image);
        Children.Add(_message);
        _plainScroll.Content = _plain;
        Children.Add(_plainScroll);
        Children.Add(_pdfPage);
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        Background = PanelPalette.Background;
        _message.Foreground = _pdfPage.Foreground = PanelPalette.DescriptionForeground;
        _plain.Foreground = PanelPalette.Foreground;
        _text.InvalidateVisual();
        if (Theme is { } theme)
        {
            TextEditorTheme.Apply(_editor, theme);
        }
    }

    public void Show(string? path, bool isDirectory, long? folderSize)
    {
        using var measure = Core.Diagnostics.Perf.Enabled && path is not null
            ? Core.Diagnostics.Perf.Measure("quickview.show", isDirectory ? "folder" : Reporting.PerfDiagnostics.ExtensionKind(path))
            : default;
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) && !isDirectory)
        {
            return;
        }

        _path = path;
        _document?.Dispose();
        _document = null;
        ClosePdf();
        _text.Document = null;
        _image.Source = null;
        _text.Visibility = _image.Visibility = _message.Visibility = _plainScroll.Visibility = _editor.Visibility = Visibility.Collapsed;
        _editor.Text = string.Empty;
        if (path is null)
        {
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        if (isDirectory)
        {
            _message.Text = $"{Path.GetFileName(path.TrimEnd('\\'))}\n\n" + (folderSize is { } size ? $"Size: {PanelColumns.FormatCompactSize(size, culture)}" : "Folder (F3 computes its size)");
            _message.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            if (ViewerPage.ImageExtensions.Contains(Path.GetExtension(path)))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                _image.Source = image;
                _image.Visibility = Visibility.Visible;
                return;
            }

            if (PdfViewerPage.IsPdf(path))
            {
                _ = ShowPdfAsync(path);
                return;
            }

            if (Settings is { } settings && ViewerPage.FitsTextMode(path, settings.ViewerTextModeMaxMB))
            {
                ShowInEditor(TextFile.Load(path), settings);
                return;
            }

            _document = ViewerDocument.Open(path);
            _text.Document = _document;
            _text.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _message.Text = ex.Message;
            _message.Visibility = Visibility.Visible;
        }
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (_pdf is not null)
        {
            e.Handled = true;
            TurnPdfPage(_pdfIndex + (e.Delta < 0 ? 1 : -1));
        }
    }

    private void TurnPdfPage(int index)
    {
        if (_pdf is { PageCount: > 0 } pdf)
        {
            index = Math.Clamp(index, 0, pdf.PageCount - 1);
            if (index != _pdfIndex)
            {
                _pdfIndex = index;
                _ = RenderPdfAsync();
            }
        }
    }

    /// <summary>The keys of a panel showing this view scroll it (issue #128), as in Far.</summary>
    public void Scroll(Key key)
    {
        if (_pdf is not null)
        {
            TurnPdfPage(key switch
            {
                Key.Down or Key.PageDown or Key.Right => _pdfIndex + 1,
                Key.Up or Key.PageUp or Key.Left => _pdfIndex - 1,
                Key.Home => 0,
                Key.End => int.MaxValue,
                _ => _pdfIndex,
            });
        }
        else if (_editor.Visibility == Visibility.Visible)
        {
            switch (key)
            {
                case Key.Down: _editor.LineDown(); break;
                case Key.Up: _editor.LineUp(); break;
                case Key.PageDown: _editor.PageDown(); break;
                case Key.PageUp: _editor.PageUp(); break;
                case Key.Left: _editor.LineLeft(); break;
                case Key.Right: _editor.LineRight(); break;
                case Key.Home: _editor.ScrollToHome(); break;
                case Key.End: _editor.ScrollToEnd(); break;
            }
        }
        else if (_text.Visibility == Visibility.Visible)
        {
            switch (key)
            {
                case Key.Down: _text.ScrollLines(1); break;
                case Key.Up: _text.ScrollLines(-1); break;
                case Key.PageDown: _text.ScrollPages(1); break;
                case Key.PageUp: _text.ScrollPages(-1); break;
                case Key.Left: _text.ScrollHorizontally(-1); break;
                case Key.Right: _text.ScrollHorizontally(1); break;
                case Key.Home: _text.GoToStart(); break;
                case Key.End: _text.GoToEnd(); break;
            }
        }
        else if (_plainScroll.Visibility == Visibility.Visible)
        {
            switch (key)
            {
                case Key.Down: _plainScroll.LineDown(); break;
                case Key.Up: _plainScroll.LineUp(); break;
                case Key.PageDown: _plainScroll.PageDown(); break;
                case Key.PageUp: _plainScroll.PageUp(); break;
                case Key.Left: _plainScroll.LineLeft(); break;
                case Key.Right: _plainScroll.LineRight(); break;
                case Key.Home: _plainScroll.ScrollToHome(); break;
                case Key.End: _plainScroll.ScrollToEnd(); break;
            }
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_pdf is not null)
        {
            _ = RenderPdfAsync();
        }
    }

    private async Task ShowPdfAsync(string path)
    {
        var request = ++_pdfRequest;
        PdfPreview? pdf = null;
        try
        {
            pdf = await PdfPreview.OpenAsync(path).ConfigureAwait(true);
            if (request != _pdfRequest)
            {
                pdf.Dispose();
                return;
            }

            _pdf = pdf;
            _pdfIndex = 0;
            await RenderPdfAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (IsPdfFailure(ex))
        {
            pdf?.Dispose();
            if (request == _pdfRequest)
            {
                _pdf = null;
                ShowPdfFailure(ex);
            }
        }
    }

    private async Task RenderPdfAsync()
    {
        if (_pdf is not { } pdf || pdf.PageCount == 0)
        {
            return;
        }

        // The newest request wins: the cursor or the wheel may have moved on while this page was rendering.
        var request = ++_pdfRequest;
        var scale = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, ActualWidth - (2 * _image.Margin.Left)) * scale.DpiScaleX;
        var height = Math.Max(1, ActualHeight - (2 * _image.Margin.Top)) * scale.DpiScaleY;
        var index = _pdfIndex;
        try
        {
            var bitmap = await pdf.RenderAsync(index, width, height).ConfigureAwait(true);
            if (request == _pdfRequest)
            {
                _image.Source = bitmap;
                _image.Visibility = Visibility.Visible;
                _pdfPage.Text = $"Page {index + 1} of {pdf.PageCount}";
                _pdfPage.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (IsPdfFailure(ex))
        {
            if (request == _pdfRequest)
            {
                ShowPdfFailure(ex);
            }
        }
    }

    private void ShowPdfFailure(Exception ex)
    {
        _message.Text = $"This PDF cannot be previewed: {ex.Message}";
        _message.Visibility = Visibility.Visible;
    }

    private void ClosePdf()
    {
        _pdfRequest++;
        _pdf?.Dispose();
        _pdf = null;
        _pdfPage.Visibility = Visibility.Collapsed;
    }

    private static bool IsPdfFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException or System.Runtime.InteropServices.COMException;

    private void ShowInEditor(TextFile file, AppSettings settings)
    {
        _editor.FontFamily = new FontFamily(settings.ViewerFontFamily + ", Consolas");
        _editor.FontSize = settings.ViewerFontSize;
        _editor.WordWrap = settings.ViewerWrap;
        _editor.Options.IndentationSize = settings.EditorTabSize;
        _editor.SyntaxHighlighting = settings.EditorSyntaxHighlighting ? HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(file.Path)) : null;
        _editor.Text = file.Text;
        _editor.ScrollToHome();
        if (Theme is { } theme)
        {
            // Recolours the syntax definition just chosen.
            TextEditorTheme.Apply(_editor, theme);
        }

        _editor.Visibility = Visibility.Visible;
    }

    /// <summary>A plugin's text for the entry under the cursor (null: empty), kept scrolled where it was on a refresh.</summary>
    public void ShowText(string? text)
    {
        _path = null;
        _document?.Dispose();
        _document = null;
        ClosePdf();
        _text.Document = null;
        _image.Source = null;
        _text.Visibility = _image.Visibility = _message.Visibility = _editor.Visibility = Visibility.Collapsed;
        _plain.Text = text ?? string.Empty;
        _plainScroll.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Dispose()
    {
        ClosePdf();
        _document?.Dispose();
        _document = null;
        _path = null;
    }
}
