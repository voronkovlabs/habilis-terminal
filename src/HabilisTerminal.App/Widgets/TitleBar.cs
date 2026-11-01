using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace HabilisTerminal.App.Widgets;

/// <summary>
/// The window's own title bar, VS Code style: icon and the F9 menu headers on the left, the caption buttons on the right;
/// the empty rest drags the window like a normal caption. The maximize button answers WM_NCHITTEST with HTMAXBUTTON so
/// Windows 11 shows Snap Layouts over it.
/// </summary>
public sealed class TitleBar : DockPanel
{
    public const double BarHeight = 30;
    private const double ButtonWidth = 46;

    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCMOUSELEAVE = 0x02A2;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int WM_NCMOUSEMOVE = 0x00A0;
    private const int HTMAXBUTTON = 9;

    // Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets (Windows 10) caption glyphs.
    private const string MinimizeGlyph = "";
    private const string MaximizeGlyph = "";
    private const string RestoreGlyph = "";
    private const string CloseGlyph = "";

    private readonly Window _window;
    private readonly Border _maximize;
    private readonly TextBlock _maximizeGlyph;
    private readonly List<Border> _headers = [];

    public TitleBar(Window window, IReadOnlyList<string> menus)
    {
        _window = window;
        Height = BarHeight;
        LastChildFill = true;
        SetResourceReference(BackgroundProperty, "Habi.TitleBarBackground");

        var icon = new Image
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(10, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Source = LoadIcon(),
        };
        SetDock(icon, Dock.Left);
        Children.Add(icon);

        MenuStrip = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var title in menus)
        {
            var header = TopMenu.CreateHeader(title, BarHeight);
            var index = _headers.Count;
            header.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                MenuRequested?.Invoke(index);
            };
            header.MouseEnter += (_, _) => header.SetResourceReference(Border.BackgroundProperty, "Habi.Hover");
            header.MouseLeave += (_, _) => header.Background = Brushes.Transparent;
            WindowChrome.SetIsHitTestVisibleInChrome(header, true);
            _headers.Add(header);
            MenuStrip.Children.Add(header);
        }

        SetDock(MenuStrip, Dock.Left);
        Children.Add(MenuStrip);

        var close = CaptionButton(CloseGlyph, () => _window.Close(), isClose: true);
        _maximize = CaptionButton(MaximizeGlyph, ToggleMaximize, isClose: false);
        _maximizeGlyph = (TextBlock)_maximize.Child;
        var minimize = CaptionButton(MinimizeGlyph, () => _window.WindowState = WindowState.Minimized, isClose: false);
        foreach (var button in new[] { close, _maximize, minimize })
        {
            SetDock(button, Dock.Right);
            Children.Add(button);
        }

        Children.Add(new Border { Background = Brushes.Transparent }); // the draggable caption

        _window.StateChanged += (_, _) => UpdateMaximizeGlyph();
        _window.Activated += (_, _) => Opacity = 1;
        _window.Deactivated += (_, _) => Opacity = 0.75;
        _window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle)?.AddHook(WndProc);
    }

    /// <summary>The menu headers; the F9 drop-down is laid over them.</summary>
    public StackPanel MenuStrip { get; }

    /// <summary>A menu header was clicked: the index of its group.</summary>
    public event Action<int>? MenuRequested;

    /// <summary>Installs the chrome: no system caption, resizable edges, DWM shadow and rounded corners kept.</summary>
    public static void Install(Window window) =>
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = BarHeight,
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            UseAeroCaptionButtons = false,
            CornerRadius = default,
        });

    /// <summary>
    /// Maximized, a WindowChrome window reaches past the screen by its sizing frame plus the padded border; the content
    /// is inset by that much so nothing is cut off.
    /// </summary>
    public static Thickness MaximizedInset(Window window)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var padding = GetSystemMetrics(SM_CXPADDEDBORDER);
        var x = (GetSystemMetrics(SM_CXSIZEFRAME) + padding) / dpi.DpiScaleX;
        var y = (GetSystemMetrics(SM_CYSIZEFRAME) + padding) / dpi.DpiScaleY;
        return new Thickness(x, y, x, y);
    }

    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private void ToggleMaximize() =>
        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximizeGlyph() =>
        _maximizeGlyph.Text = _window.WindowState == WindowState.Maximized ? RestoreGlyph : MaximizeGlyph;

    private static Border CaptionButton(string glyph, Action click, bool isClose)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Habi.TitleBarForeground");
        var button = new Border { Width = ButtonWidth, Background = Brushes.Transparent, Child = text };
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.MouseEnter += (_, _) => SetHover(button, true, isClose);
        button.MouseLeave += (_, _) => SetHover(button, false, isClose);
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return button;
    }

    private static void SetHover(Border button, bool hover, bool isClose)
    {
        var text = (TextBlock)button.Child;
        if (!hover)
        {
            button.Background = Brushes.Transparent;
            text.SetResourceReference(TextBlock.ForegroundProperty, "Habi.TitleBarForeground");
        }
        else if (isClose)
        {
            button.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
            text.Foreground = Brushes.White;
        }
        else
        {
            button.SetResourceReference(Border.BackgroundProperty, "Habi.Hover");
        }
    }

    /// <summary>Snap Layouts: the maximize button must be HTMAXBUTTON to Windows, so its clicks arrive as NC messages.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST when IsOverMaximize(lParam):
                handled = true;
                SetHover(_maximize, true, isClose: false);
                return HTMAXBUTTON;
            case WM_NCMOUSEMOVE when wParam != HTMAXBUTTON:
            case WM_NCMOUSELEAVE:
                SetHover(_maximize, false, isClose: false);
                break;
            case WM_NCLBUTTONDOWN when wParam == HTMAXBUTTON:
                handled = true;
                break;
            case WM_NCLBUTTONUP when wParam == HTMAXBUTTON:
                handled = true;
                ToggleMaximize();
                break;
        }

        return IntPtr.Zero;
    }

    private bool IsOverMaximize(IntPtr lParam)
    {
        if (!_maximize.IsVisible)
        {
            return false;
        }

        var x = unchecked((short)(long)lParam);
        var y = unchecked((short)((long)lParam >> 16));
        var point = _maximize.PointFromScreen(new Point(x, y));
        return point.X >= 0 && point.Y >= 0 && point.X < _maximize.ActualWidth && point.Y < _maximize.ActualHeight;
    }

    private static BitmapFrame? LoadIcon()
    {
        try
        {
            var decoder = new IconBitmapDecoder(new Uri("pack://application:,,,/habi;component/Assets/habi.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderBy(frame => Math.Abs(frame.PixelWidth - 32)).First();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException)
        {
            return null;
        }
    }
}
