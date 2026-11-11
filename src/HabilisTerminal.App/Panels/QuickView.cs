using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using HabilisTerminal.App.Editing;
using HabilisTerminal.App.Theming;
using HabilisTerminal.App.Viewing;
using HabilisTerminal.Core.Editing;
using HabilisTerminal.Core.Settings;
using HabilisTerminal.Core.Viewing;
using HabilisTerminal.Themes;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;

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
    private readonly ScrollViewer _plainScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsVisible = false };
    private readonly TextEditor _editor = new()
    {
        IsReadOnly = true,
        ShowLineNumbers = true,
        Focusable = false,
        IsVisible = false,
    };

    private readonly TextBlock _pdfPage = new()
    {
        Margin = new Thickness(12, 6, 12, 6),
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        IsVisible = false,
    };

    private ViewerDocument? _document;
    private string? _path;
    private PdfPreview? _pdf;
    private int _pdfIndex;
    private int _pdfRequest;

    /// <summary>The settings and colour theme of the text mode, set by the window (like <see cref="ViewerView.SetFont"/>).</summary>
    public static AppSettings? Settings { get; set; }

    public static ColorTheme? TextTheme { get; set; }

    public QuickView()
    {
        AddHandler(PointerWheelChangedEvent, OnPreviewMouseWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Children.Add(_text);
        _editor.TextArea.Focusable = false; // the keyboard stays in the panels
        Children.Add(_editor);
        Children.Add(_image);
        Widgets.UiZoom.SnapToPixels(_image);
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
        if (TextTheme is { } theme)
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
        _text.IsVisible = _image.IsVisible = _message.IsVisible = _plainScroll.IsVisible = _editor.IsVisible = false;
        _editor.Text = string.Empty;
        if (path is null)
        {
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        if (isDirectory)
        {
            _message.Text = $"{Path.GetFileName(path.TrimEnd('\\'))}\n\n" + (folderSize is { } size ? $"Size: {PanelColumns.FormatCompactSize(size, culture)}" : "Folder (F3 computes its size)");
            _message.IsVisible = true;
            return;
        }

        try
        {
            if (ImagePage.IsImage(path))
            {
                _image.Source = new Avalonia.Media.Imaging.Bitmap(path);
                _image.IsVisible = true;
                return;
            }

            if (PdfImagePage.IsPdf(path))
            {
                _ = ShowPdfAsync(path);
                return;
            }

            if (Settings is { } settings && ViewerPage.FitsTextMode(path, settings.ViewerTextModeMaxMB) && !Core.Editing.LongLines.HasHugeLine(path))
            {
                ShowInEditor(TextFile.Load(path), settings);
                return;
            }

            _document = ViewerDocument.Open(path);
            _text.Document = _document;
            _text.IsVisible = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _message.Text = ex.Message;
            _message.IsVisible = true;
        }
    }

    private void OnPreviewMouseWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_pdf is not null)
        {
            e.Handled = true;
            TurnPdfPage(_pdfIndex + (e.Delta.Y < 0 ? 1 : -1));
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
        else if (_editor.IsVisible)
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
        else if (_text.IsVisible)
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
        else if (_plainScroll.IsVisible)
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

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
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
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = Math.Max(1, Bounds.Width - (2 * _image.Margin.Left)) * scale * Widgets.UiZoom.Factor;
        var height = Math.Max(1, Bounds.Height - (2 * _image.Margin.Top)) * scale * Widgets.UiZoom.Factor;
        var index = _pdfIndex;
        try
        {
            var bitmap = await pdf.RenderAsync(index, width, height).ConfigureAwait(true);
            if (request == _pdfRequest)
            {
                Widgets.UiZoom.Unscale(_image); // drawn for the zoomed pixels (issue #237)
                _image.Source = bitmap;
                _image.IsVisible = true;
                _pdfPage.Text = $"Page {index + 1} of {pdf.PageCount}";
                _pdfPage.IsVisible = true;
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
        _message.IsVisible = true;
    }

    private void ClosePdf()
    {
        _pdfRequest++;
        _pdf?.Dispose();
        _pdf = null;
        _pdfPage.IsVisible = false;
    }

    private static bool IsPdfFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException or System.Runtime.InteropServices.COMException;

    private void ShowInEditor(TextFile file, AppSettings settings)
    {
        _editor.FontFamily = new FontFamily(settings.ViewerFontFamily + ", Consolas");
        _editor.FontSize = settings.ViewerFontSize;
        _editor.WordWrap = settings.ViewerWrap;
        _editor.Options.IndentationSize = settings.EditorTabSize;
        SyntaxColors.Set(_editor, file.Path, settings.EditorSyntaxHighlighting && !Core.Editing.LongLines.TooLongToColor(file.Text));
        _editor.Text = file.Text;
        _editor.ScrollToHome();
        if (TextTheme is { } theme)
        {
            // Recolours the syntax definition just chosen.
            TextEditorTheme.Apply(_editor, theme);
        }

        _editor.IsVisible = true;
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
        _text.IsVisible = _image.IsVisible = _message.IsVisible = _editor.IsVisible = false;
        _plain.Text = text ?? string.Empty;
        _plainScroll.IsVisible = !(text is null);
    }

    public void Dispose()
    {
        ClosePdf();
        _document?.Dispose();
        _document = null;
        _path = null;
    }
}
