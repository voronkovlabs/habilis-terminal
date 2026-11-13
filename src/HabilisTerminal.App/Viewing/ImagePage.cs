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
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _info = new();
    private readonly TextBlock _zoom = new();
    private readonly Border _previous;
    private readonly Border _next;
    private string _details = string.Empty;
    private int _index;

    /// <param name="files">The pictures to step through, in order; <paramref name="index"/> is the one to show.</param>
    public ImagePage(IReadOnlyList<string> files, int index)
    {
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _files = files.Count > 0 ? files : throw new ArgumentException("No picture.", nameof(files));
        Focusable = true;
        ;
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

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var key = e.Key;
        var modifiers = Keyboard.Modifiers;
        e.Handled = true;
        switch (key)
        {
            case Key.Escape or Key.F10 when modifiers == KeyModifiers.None:
            case Key.F3 when modifiers == KeyModifiers.Alt:
                CloseRequested?.Invoke();
                break;
            case Key.F3 when modifiers == KeyModifiers.None:
                ViewRequested?.Invoke(FilePath);
                break;
            case Key.Left or Key.PageUp when modifiers == KeyModifiers.None:
                Show(_index - 1);
                break;
            case Key.Right or Key.PageDown or Key.Space when modifiers == KeyModifiers.None:
                Show(_index + 1);
                break;
            case Key.Home when modifiers == KeyModifiers.None:
                Show(0);
                break;
            case Key.End when modifiers == KeyModifiers.None:
                Show(_files.Count - 1);
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    /// <summary>Fitted, the wheel steps through the pictures; zoomed, the view has scrolled with it already.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!e.Handled && Keyboard.Modifiers == KeyModifiers.None)
        {
            e.Handled = true;
            if (_wheel.Take(e.Delta.Y) is var steps and not 0)
            {
                Show(_index - steps); // a trackpad's fractions add up to whole pictures (#275)
            }
        }
    }

    private readonly Input.WheelSteps _wheel = new();

    private void Show(int index)
    {
        index = Math.Clamp(index, 0, _files.Count - 1);
        var first = string.IsNullOrEmpty(_title.Text);
        if (index == _index && !first)
        {
            return;
        }

        _index = index;
        var path = FilePath;
        _title.Text = _files.Count > 1 ? $"{path}  ({index + 1} of {_files.Count})" : path;
        try
        {
            var image = new Avalonia.Media.Imaging.Bitmap(path);
            _details = $"{image.PixelWidth} × {image.PixelHeight}  ·  {PanelColumns.FormatCompactSize(new FileInfo(path).Length, CultureInfo.CurrentCulture)}";
            _info.Text = _details;
            _view.Show(image, new Size(image.PixelWidth, image.PixelHeight));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _info.Text = $"Cannot show the image: {ex.Message}";
            _view.Show(null, default);
        }

        _previous.IsVisible = _files.Count > 1;
        _next.Visibility = _previous.Visibility;
        _previous.Opacity = index > 0 ? 1 : 0.3;
        _next.Opacity = index < _files.Count - 1 ? 1 : 0.3;
    }

    /// <summary>A round button on the picture's edge, as in Windows Photos.</summary>
    private static Border Arrow(string glyph, HorizontalAlignment side, Action click)
    {
        // Segoe Fluent Icons exists on Windows only (#261): elsewhere the codicons' chevrons, drawn as outlines.
        Control text = OperatingSystem.IsWindows()
            ? new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
            : new Avalonia.Controls.Shapes.Path
            {
                Data = Theming.Codicon.GeometryOf(side == HorizontalAlignment.Left ? "" : ""),
                Fill = Brushes.White,
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
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
        button.PointerEntered += (_, _) => button.Background = hover;
        button.PointerExited += (_, _) => button.Background = idle;
        button.PointerPressed += (_, e) => e.Handled = true; // not a drag or a double click of the picture
        button.PointerReleased += (_, e) =>
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
