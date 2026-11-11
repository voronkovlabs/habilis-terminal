using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HabilisTerminal.App.Theming;

namespace HabilisTerminal.App.Viewing;

/// <summary>
/// Alt+F3 on a PDF (issues #114, #231): one page at a time through <see cref="PdfPreview"/>. The Edge viewer starts a browser process
/// and reads the whole file, about 60 ms per MB, so it comes only on Ctrl+F, for search and text selection.
/// PgUp / PgDn, arrows and the wheel turn pages, Home / End go to the ends, F3 the text viewer, Esc / F10 / Alt+F3 close.
/// </summary>
public sealed class PdfImagePage : DockPanel, IPage, IDisposable
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    private readonly Grid _content = new();
    private readonly TextBlock _info = new();
    private PdfPreview? _pdf;
    private int _index;
    private int _request;
    private bool _closed;

    public PdfImagePage(string path)
    {
        Focusable = true;
        FocusVisualStyle = null;
        Background = PanelPalette.Background;
        LastChildFill = true;

        var title = new TextBlock { Text = path, FontWeight = FontWeights.SemiBold };
        var hint = new TextBlock { Text = "PgUp PgDn page · Home End · Ctrl+F find, select text · F3 as text · Esc close" };
        foreach (var text in new[] { title, _info, hint })
        {
            text.FontFamily = new FontFamily("Segoe UI");
            text.FontSize = 12;
            text.Foreground = text == title ? PanelPalette.Foreground : PanelPalette.DescriptionForeground;
            text.Margin = new Thickness(0, 0, 16, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
        }

        var header = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
        DockPanel.SetDock(hint, Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(title);
        header.Children.Add(_info);
        var headerBorder = new Border { Child = header, BorderBrush = PanelPalette.Border, BorderThickness = new Thickness(0, 0, 0, 1), Background = PanelPalette.HeaderBackground };
        DockPanel.SetDock(headerBorder, Dock.Top);
        Children.Add(headerBorder);

        _content.Children.Add(_image);
        _content.SizeChanged += (_, _) => _ = RenderAsync();
        Widgets.UiZoom.Changed += OnZoomChanged;
        Widgets.UiZoom.SnapToPixels(_image);
        Children.Add(_content);
        _ = OpenAsync(path);
    }

    /// <summary>Esc / F10 / Alt+F3.</summary>
    public event Action? CloseRequested;

    /// <summary>F3: the same file in the text viewer.</summary>
    public event Action? ViewRequested;

    /// <summary>Ctrl+F: the same file in the Edge viewer, at this zero-based page.</summary>
    public event Action<int>? SearchRequested;

    public void FocusContent() => Focus();

    public void Dispose()
    {
        _closed = true;
        Widgets.UiZoom.Changed -= OnZoomChanged;
        _request++;
        _pdf?.Dispose();
        _pdf = null;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SearchRequested?.Invoke(_index);
            return;
        }

        if (key == Key.F3 && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            e.Handled = true;
            CloseRequested?.Invoke();
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        e.Handled = true;
        switch (key)
        {
            case Key.Escape or Key.F10:
                CloseRequested?.Invoke();
                break;
            case Key.F3:
                ViewRequested?.Invoke();
                break;
            case Key.PageDown or Key.Down or Key.Right or Key.Space:
                GoTo(_index + 1);
                break;
            case Key.PageUp or Key.Up or Key.Left:
                GoTo(_index - 1);
                break;
            case Key.Home:
                GoTo(0);
                break;
            case Key.End:
                GoTo(int.MaxValue);
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        e.Handled = true;
        GoTo(_index + (e.Delta < 0 ? 1 : -1));
    }

    private void GoTo(int index)
    {
        if (_pdf is { PageCount: > 0 } pdf)
        {
            index = Math.Clamp(index, 0, pdf.PageCount - 1);
            if (index != _index)
            {
                _index = index;
                _ = RenderAsync();
            }
        }
    }

    private async Task OpenAsync(string path)
    {
        try
        {
            var pdf = await PdfPreview.OpenAsync(path).ConfigureAwait(true);
            if (_closed)
            {
                pdf.Dispose();
                return;
            }

            _pdf = pdf;
            await RenderAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            _info.Text = $"This PDF cannot be shown: {ex.Message}";
        }
    }

    private async Task RenderAsync()
    {
        if (_pdf is not { PageCount: > 0 } pdf || _content.ActualWidth < 1 || _content.ActualHeight < 1)
        {
            return;
        }

        // The newest request wins: pages can be turned faster than they render.
        var request = ++_request;
        var index = _index;
        var scale = VisualTreeHelper.GetDpi(this);
        _info.Text = $"Page {index + 1} of {pdf.PageCount}";
        try
        {
            // In the interface zoom (issue #237) the page is drawn at the pixels it covers, not enlarged from a smaller one.
            var zoom = Widgets.UiZoom.Factor;
            var bitmap = await pdf.RenderAsync(index, _content.ActualWidth * scale.DpiScaleX * zoom, _content.ActualHeight * scale.DpiScaleY * zoom).ConfigureAwait(true);
            if (request == _request)
            {
                Widgets.UiZoom.Unscale(_image);
                _image.Source = bitmap;
            }
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            if (request == _request)
            {
                _info.Text = $"Page {index + 1} cannot be shown: {ex.Message}";
            }
        }
    }

    private void OnZoomChanged() => _ = RenderAsync();

    private static bool IsFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException or COMException;
}
