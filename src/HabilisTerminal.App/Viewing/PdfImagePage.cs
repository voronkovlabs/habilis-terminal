using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HabilisTerminal.App.Theming;

namespace HabilisTerminal.App.Viewing;

/// <summary>
/// F3 on a large PDF (issue #113): one page at a time through <see cref="PdfPreview"/>. The Edge viewer reads the whole
/// file before it shows anything, about 60 ms per MB; this opens at once but has no search or text selection.
/// PgUp / PgDn, arrows and the wheel turn pages, Home / End go to the ends, Esc / F10 / F3 close.
/// </summary>
public sealed class PdfImagePage : DockPanel, IPage, IDisposable
{
    private const long LargeFileBytes = 50L * 1024 * 1024;

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
        var hint = new TextBlock { Text = "Large file: no search · PgUp PgDn page · Home End · Esc close" };
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
        Children.Add(_content);
        _ = OpenAsync(path);
    }

    /// <summary>Esc / F10 / F3.</summary>
    public event Action? CloseRequested;

    public static bool IsLarge(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length >= LargeFileBytes;
    }

    public void FocusContent() => Focus();

    public void Dispose()
    {
        _closed = true;
        _request++;
        _pdf?.Dispose();
        _pdf = null;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        e.Handled = true;
        switch (key)
        {
            case Key.Escape or Key.F10 or Key.F3:
                CloseRequested?.Invoke();
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
            var bitmap = await pdf.RenderAsync(index, _content.ActualWidth * scale.DpiScaleX, _content.ActualHeight * scale.DpiScaleY).ConfigureAwait(true);
            if (request == _request)
            {
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

    private static bool IsFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException or COMException;
}
