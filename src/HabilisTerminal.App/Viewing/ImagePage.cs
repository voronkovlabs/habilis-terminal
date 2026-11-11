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
/// Alt+F3 on a picture (issue #231): the image through the WPF decoders, fitted to the window and never enlarged.
/// F3 shows the same file in the text viewer; Esc / F10 / Alt+F3 close.
/// </summary>
public sealed class ImagePage : DockPanel, IPage
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff",
    };

    private readonly TextBlock _info = new();

    public ImagePage(string path)
    {
        FilePath = path;
        Focusable = true;
        FocusVisualStyle = null;
        Background = PanelPalette.Background;
        LastChildFill = true;

        var title = new TextBlock { Text = path, FontWeight = FontWeights.SemiBold };
        var hint = new TextBlock { Text = "F3 as text · Esc close" };
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
        Children.Add(CreateImage(path));
    }

    /// <summary>Esc / F10 / Alt+F3.</summary>
    public event Action? CloseRequested;

    /// <summary>F3: the same file in the text viewer.</summary>
    public event Action<string>? ViewRequested;

    public string FilePath { get; }

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
            default:
                e.Handled = false;
                break;
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
