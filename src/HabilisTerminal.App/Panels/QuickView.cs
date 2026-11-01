using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HabilisTerminal.App.Theming;
using HabilisTerminal.App.Viewing;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.App.Panels;

/// <summary>Ctrl+Q (SPEC §9): the current file of the other panel as text or image, folders as a summary.</summary>
public sealed class QuickView : Grid, IDisposable
{
    private readonly ViewerView _text = new();
    private readonly Image _image = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(8) };
    private readonly TextBlock _message = new() { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Segoe UI"), FontSize = 13 };
    private ViewerDocument? _document;
    private string? _path;

    public QuickView()
    {
        Children.Add(_text);
        Children.Add(_image);
        Children.Add(_message);
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        Background = PanelPalette.Background;
        _message.Foreground = PanelPalette.DescriptionForeground;
        _text.InvalidateVisual();
    }

    public void Show(string? path, bool isDirectory, long? folderSize)
    {
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) && !isDirectory)
        {
            return;
        }

        _path = path;
        _document?.Dispose();
        _document = null;
        _text.Document = null;
        _image.Source = null;
        _text.Visibility = _image.Visibility = _message.Visibility = Visibility.Collapsed;
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

    public void Dispose()
    {
        _document?.Dispose();
        _document = null;
        _path = null;
    }
}
