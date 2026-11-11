using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using HabilisTerminal.App.Theming;

namespace HabilisTerminal.App.Viewing;

/// <summary>
/// Alt+F3 on a PDF (issues #114, #231): one page at a time through <see cref="PdfPreview"/>. The Edge viewer starts a browser process
/// and reads the whole file, about 60 ms per MB, so it comes only on Ctrl+F, for search and text selection.
/// PgUp / PgDn, arrows and the wheel turn pages, Home / End go to the ends, F3 the text viewer, Esc / F10 / Alt+F3 close.
/// Ctrl+wheel zooms the page (issue #241); it is drawn again at the zoomed pixels, so it stays sharp.
/// </summary>
public sealed class PdfImagePage : DockPanel, IPage, IDisposable, IContentZoom
{
    /// <summary>A zoomed page is drawn at most this many pixels on its long side, to keep memory in bounds.</summary>
    private const double MaxRenderPixels = 8000;

    private readonly ZoomPanView _view = new(enlargeToFit: true);
    private readonly TextBlock _info = new();
    private readonly Avalonia.Threading.DispatcherTimer _sharpen;
    private PdfPreview? _pdf;
    private int _index;
    private int _request;
    private bool _closed;
    private Size _rendered;
    private Size _pagePixels;

    public PdfImagePage(string path)
    {
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Focusable = true;
        ;
        Background = PanelPalette.Background;
        LastChildFill = true;

        var title = new TextBlock { Text = path, FontWeight = FontWeight.SemiBold };
        var hint = new TextBlock { Text = "PgUp PgDn page · Ctrl+wheel zoom · Ctrl+F find, select text · F3 as text · Esc close" };
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

        Children.Add(_view);
        _sharpen = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(120), Avalonia.Threading.DispatcherPriority.Background, (_, _) => Sharpen());
        _view.ViewChanged += () =>
        {
            ShowInfo();
            _sharpen.Stop();
            _sharpen.Start();
        };
        _ = OpenAsync(path);
    }

    public static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Esc / F10 / Alt+F3.</summary>
    public event Action? CloseRequested;

    /// <summary>F3: the same file in the text viewer.</summary>
    public event Action? ViewRequested;

    /// <summary>Ctrl+F: the same file in the Edge viewer, at this zero-based page.</summary>
    public event Action<int>? SearchRequested;

    public ZoomPanView ZoomView => _view;

    public void FocusContent() => Focus();

    public void Dispose()
    {
        _closed = true;
        _sharpen.Stop();
        _request++;
        _pdf?.Dispose();
        _pdf = null;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var key = e.Key;
        if (key == Key.F && Keyboard.Modifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            SearchRequested?.Invoke(_index);
            return;
        }

        if (key == Key.F3 && Keyboard.Modifiers == KeyModifiers.Alt)
        {
            e.Handled = true;
            CloseRequested?.Invoke();
            return;
        }

        if (Keyboard.Modifiers != KeyModifiers.None)
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

    /// <summary>Fitted, the wheel turns pages; zoomed, the view has scrolled with it already.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!e.Handled && Keyboard.Modifiers == KeyModifiers.None)
        {
            e.Handled = true;
            GoTo(_index + (e.Delta.Y < 0 ? 1 : -1));
        }
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

    /// <summary>A new page, fitted: drawn at the pixels it will cover, then shown.</summary>
    private async Task RenderAsync()
    {
        if (_pdf is not { PageCount: > 0 } pdf || _view.ActualWidth < 1 || _view.ActualHeight < 1)
        {
            return;
        }

        // The newest request wins: pages can be turned faster than they render.
        var request = ++_request;
        var index = _index;
        try
        {
            var dpi = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var size = pdf.PageSize(index);
            var pixels = new Size(size.Width * dpi, size.Height * dpi);
            var fitted = _view.FittedPixels(pixels);
            var bitmap = await pdf.RenderAsync(index, fitted.Width, fitted.Height).ConfigureAwait(true);
            if (request == _request)
            {
                _pagePixels = pixels;
                _rendered = new Size(bitmap.PixelWidth, bitmap.PixelHeight);
                _view.Show(bitmap, pixels);
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

    /// <summary>After a zoom or a resize settles: the page again at the pixels it covers now, the view kept.</summary>
    private async void Sharpen()
    {
        _sharpen.Stop();
        if (_pdf is not { PageCount: > 0 } pdf)
        {
            return;
        }

        // Opened before the view had a size: the first page is still to be drawn.
        if (_pagePixels.Width < 1)
        {
            await RenderAsync().ConfigureAwait(true);
            return;
        }

        var wanted = _view.DisplayPixels;
        var longSide = Math.Max(wanted.Width, wanted.Height);
        if (longSide > MaxRenderPixels)
        {
            wanted = new Size(wanted.Width * MaxRenderPixels / longSide, wanted.Height * MaxRenderPixels / longSide);
        }

        if (Math.Abs(wanted.Width - _rendered.Width) < 2 && Math.Abs(wanted.Height - _rendered.Height) < 2)
        {
            return;
        }

        var request = ++_request;
        var index = _index;
        try
        {
            var bitmap = await pdf.RenderAsync(index, wanted.Width, wanted.Height).ConfigureAwait(true);
            if (request == _request && index == _index)
            {
                _rendered = new Size(bitmap.PixelWidth, bitmap.PixelHeight);
                _view.Replace(bitmap);
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

    private void ShowInfo()
    {
        if (_pdf is { PageCount: > 0 } pdf)
        {
            _info.Text = $"Page {_index + 1} of {pdf.PageCount}  ·  {_view.PercentText}";
        }
    }

    private static bool IsFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException or COMException;
}
