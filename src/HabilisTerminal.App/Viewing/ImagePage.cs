using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Theming;

namespace HabilisTerminal.App.Viewing;

/// <summary>
/// Alt+F3 on a picture (issues #231, #241, #242): the image through the WPF decoders, fitted to the window and never
/// enlarged to fit; Ctrl+wheel zooms it (<see cref="ZoomPanView"/>). Left / Right, the wheel and the arrows on the edges go
/// to the previous / next picture of the folder in the panel's order, Home / End to the first / last. F3 shows the file
/// in the text viewer; Esc / F10 / Alt+F3 close.
/// </summary>
public sealed class ImagePage : DockPanel, IPage, IContentZoom
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff",
    };

    private readonly IReadOnlyList<string> _files;
    private readonly ZoomPanView _view = new(enlargeToFit: false);
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _info = new();
    private readonly TextBlock _zoom = new();
    private readonly Border _previous;
    private readonly Border _next;
    private string _details = string.Empty;
    private int _index;

    /// <param name="files">The pictures to step through, in order; <paramref name="index"/> is the one to show.</param>
    public ImagePage(IReadOnlyList<string> files, int index)
    {
        _files = files.Count > 0 ? files : throw new ArgumentException("No picture.", nameof(files));
        Focusable = true;
        FocusVisualStyle = null;
        Background = PanelPalette.Background;
        LastChildFill = true;

        var hint = new TextBlock { Text = files.Count > 1 ? "← → picture · Ctrl+wheel zoom · F3 as text · Esc close" : "Ctrl+wheel zoom · F3 as text · Esc close" };
        foreach (var text in new[] { _title, _info, _zoom, hint })
        {
            text.FontFamily = new FontFamily("Segoe UI");
            text.FontSize = 12;
            text.Foreground = text == _title ? PanelPalette.Foreground : PanelPalette.DescriptionForeground;
            text.Margin = new Thickness(0, 0, 16, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
        }

        var header = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
        DockPanel.SetDock(hint, Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(_title);
        header.Children.Add(_info);
        header.Children.Add(_zoom);
        var headerBorder = new Border { Child = header, BorderBrush = PanelPalette.Border, BorderThickness = new Thickness(0, 0, 0, 1), Background = PanelPalette.HeaderBackground };
        DockPanel.SetDock(headerBorder, Dock.Top);
        Children.Add(headerBorder);

        _previous = Arrow("", HorizontalAlignment.Left, () => Show(_index - 1));
        _next = Arrow("", HorizontalAlignment.Right, () => Show(_index + 1));
        var area = new Grid();
        area.Children.Add(_view);
        area.Children.Add(_previous);
        area.Children.Add(_next);
        Children.Add(area);

        _view.ViewChanged += () => _zoom.Text = _view.PercentText;
        Show(Math.Clamp(index, 0, files.Count - 1));
    }

    /// <summary>Esc / F10 / Alt+F3.</summary>
    public event Action? CloseRequested;

    /// <summary>F3: the picture shown, in the text viewer.</summary>
    public event Action<string>? ViewRequested;

    /// <summary>The picture shown now.</summary>
    public string FilePath => _files[_index];

    public ZoomPanView ZoomView => _view;

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path));

    public void FocusContent() => Focus();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        e.Handled = true;
        switch (key)
        {
            case Key.Escape or Key.F10 when modifiers == ModifierKeys.None:
            case Key.F3 when modifiers == ModifierKeys.Alt:
                CloseRequested?.Invoke();
                break;
            case Key.F3 when modifiers == ModifierKeys.None:
                ViewRequested?.Invoke(FilePath);
                break;
            case Key.Left or Key.PageUp when modifiers == ModifierKeys.None:
                Show(_index - 1);
                break;
            case Key.Right or Key.PageDown or Key.Space when modifiers == ModifierKeys.None:
                Show(_index + 1);
                break;
            case Key.Home when modifiers == ModifierKeys.None:
                Show(0);
                break;
            case Key.End when modifiers == ModifierKeys.None:
                Show(_files.Count - 1);
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    /// <summary>Fitted, the wheel steps through the pictures; zoomed, the view has scrolled with it already.</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!e.Handled && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Show(_index + (e.Delta < 0 ? 1 : -1));
        }
    }

    private void Show(int index)
    {
        index = Math.Clamp(index, 0, _files.Count - 1);
        var first = _title.Text.Length == 0;
        if (index == _index && !first)
        {
            return;
        }

        _index = index;
        var path = FilePath;
        _title.Text = _files.Count > 1 ? $"{path}  ({index + 1} of {_files.Count})" : path;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            _details = $"{image.PixelWidth} × {image.PixelHeight}  ·  {PanelColumns.FormatCompactSize(new FileInfo(path).Length, CultureInfo.CurrentCulture)}";
            _info.Text = _details;
            _view.Show(image, new Size(image.PixelWidth, image.PixelHeight));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _info.Text = $"Cannot show the image: {ex.Message}";
            _view.Show(null, default);
        }

        _previous.Visibility = _files.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _next.Visibility = _previous.Visibility;
        _previous.Opacity = index > 0 ? 1 : 0.3;
        _next.Opacity = index < _files.Count - 1 ? 1 : 0.3;
    }

    /// <summary>A round button on the picture's edge, as in Windows Photos.</summary>
    private static Border Arrow(string glyph, HorizontalAlignment side, Action click)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var idle = new SolidColorBrush(Color.FromArgb(0x70, 0x20, 0x20, 0x20));
        var hover = new SolidColorBrush(Color.FromArgb(0xC0, 0x30, 0x30, 0x30));
        var button = new Border
        {
            Child = text,
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Background = idle,
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12),
            Cursor = Cursors.Hand,
        };
        button.MouseEnter += (_, _) => button.Background = hover;
        button.MouseLeave += (_, _) => button.Background = idle;
        button.MouseLeftButtonDown += (_, e) => e.Handled = true; // not a drag or a double click of the picture
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return button;
    }
}

/// <summary>A page with its own zoom (issue #241): Ctrl+wheel and Ctrl+Alt+= / − / 0 go to it, not to the interface.</summary>
public interface IContentZoom
{
    ZoomPanView ZoomView { get; }
}
