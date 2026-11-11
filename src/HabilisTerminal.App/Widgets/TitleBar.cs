using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using HabilisTerminal.App.Theming;

namespace HabilisTerminal.App.Widgets;

/// <summary>
/// The window's own title bar, VS Code style: icon and the F9 menu headers on the left, the caption buttons on the right;
/// the empty rest drags the window like a normal caption. On Avalonia (issue #253) the caption and the buttons carry
/// window decoration roles, so Windows treats them as its own: the caption drags and maximizes on a double click, and
/// the maximize button shows Snap Layouts.
/// </summary>
public sealed class TitleBar : DockPanel
{
    public const double BarHeight = 30;

    /// <summary>What the box in the title bar says.</summary>
    public const string CommandBoxText = "Commands and paths · Ctrl+Shift+P";

    private const double CommandBoxMaxWidth = 520;
    private const double CommandBoxMinWidth = 200;
    private const double CommandBoxHeight = 22;
    private const double ButtonWidth = 46;

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
        this.SetResourceReference(BackgroundProperty, "Habi.TitleBarBackground");

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
            header.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                MenuRequested?.Invoke(index);
            };
            header.PointerEntered += (_, _) => header.SetResourceReference(Border.BackgroundProperty, "Habi.TitleBarHover");
            header.PointerExited += (_, _) => header.Background = Brushes.Transparent;
            _headers.Add(header);
            MenuStrip.Children.Add(header);
        }

        SetDock(MenuStrip, Dock.Left);
        Children.Add(MenuStrip);

        var close = CaptionButton(CloseGlyph, () => _window.Close(), isClose: true);
        _maximize = CaptionButton(MaximizeGlyph, ToggleMaximize, isClose: false);
        _maximizeGlyph = (TextBlock)_maximize.Child!;
        var minimize = CaptionButton(MinimizeGlyph, () => _window.WindowState = WindowState.Minimized, isClose: false);
        var feedback = FeedbackButton();
        foreach (var button in new[] { close, _maximize, minimize, feedback, ZoomButton() })
        {
            SetDock(button, Dock.Right);
            Children.Add(button);
        }

        // The draggable caption, with the command line's box in the middle of the window.
        CommandBox = MakeCommandBox();
        var caption = new Grid { Background = Brushes.Transparent };
        WindowDecorationProperties.SetElementRole(caption, WindowDecorationsElementRole.TitleBar);
        caption.Children.Add(CommandBox);
        Children.Add(caption);
        caption.SizeChanged += (_, _) => PlaceCommandBox(caption);
        SizeChanged += (_, _) => PlaceCommandBox(caption);

        _window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty)
            {
                UpdateMaximizeGlyph();
            }
        };
        _window.Activated += (_, _) => Opacity = 1;
        _window.Deactivated += (_, _) => Opacity = 0.75;
    }

    /// <summary>The menu headers; the F9 drop-down is laid over them.</summary>
    public StackPanel MenuStrip { get; }

    /// <summary>A menu header was clicked: the index of its group.</summary>
    public event Action<int>? MenuRequested;

    /// <summary>The command line's box (issue #155): typing happens in a pick that opens over it.</summary>
    public Border CommandBox { get; }

    /// <summary>The command line's box was clicked.</summary>
    public event Action? CommandBoxRequested;

    /// <summary>The feedback icon left of the caption buttons was clicked.</summary>
    public event Action? FeedbackRequested;

    private Border MakeCommandBox()
    {
        var text = new TextBlock { Text = CommandBoxText, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Placeholder");
        var box = new Border
        {
            Height = CommandBoxHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = text,
            Cursor = new Cursor(StandardCursorType.Ibeam),
        };
        box.SetResourceReference(Border.BackgroundProperty, "Habi.InputBackground");
        box.SetResourceReference(Border.BorderBrushProperty, "Habi.InputBorder");
        WindowDecorationProperties.SetElementRole(box, WindowDecorationsElementRole.User);
        box.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            CommandBoxRequested?.Invoke();
        };
        return box;
    }

    /// <summary>In the middle of the window where it fits; else moved aside; hidden when the window is too narrow for it.</summary>
    private void PlaceCommandBox(Control caption)
    {
        const double Margin = 8;
        var available = caption.Bounds.Width - (2 * Margin);
        if (available < CommandBoxMinWidth)
        {
            CommandBox.IsVisible = false;
            return;
        }

        var width = Math.Min(CommandBoxMaxWidth, available);
        var captionLeft = caption.TranslatePoint(default, this)?.X ?? 0;
        var left = ((Bounds.Width - width) / 2) - captionLeft;
        CommandBox.Width = width;
        CommandBox.Margin = new Thickness(Math.Clamp(left, Margin, Math.Max(Margin, caption.Bounds.Width - width - Margin)), 0, 0, 0);
        CommandBox.IsVisible = true;
    }

    /// <summary>
    /// The window draws its own caption: the client area takes the title bar's place, the frame stays Windows'.
    /// Avalonia would draw a title bar of its own over ours (title and caption buttons): its decorations get an empty
    /// template, so only our bar shows, and the roles above tell Windows what each part is.
    /// </summary>
    public static void Install(Window window)
    {
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = BarHeight;
        window.WindowDecorations = WindowDecorations.Full;
        var theme = new Avalonia.Styling.ControlTheme(typeof(WindowDrawnDecorations));
        theme.Setters.Add(new Avalonia.Styling.Setter(WindowDrawnDecorations.TemplateProperty, new NoDrawnDecorations()));
        window.WindowDecorationsTheme = theme;
    }

    /// <summary>Avalonia's drawn decorations with nothing in them.</summary>
    private sealed class NoDrawnDecorations : IWindowDrawnDecorationsTemplate
    {
        public Avalonia.Controls.Templates.TemplateResult<WindowDrawnDecorationsContent> Build() => new(new WindowDrawnDecorationsContent(), new NameScope());

        object? Avalonia.Styling.ITemplate.Build() => Build();
    }

    /// <summary>The window's messages, for the title bar: nothing to do any more (the roles do it), kept for later needs.</summary>
    public static IntPtr OnWindowMessage(Window window, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled) => IntPtr.Zero;

    /// <summary>Maximized, the content keeps clear of the screen edge the frame would cover.</summary>
    public static Thickness MaximizedInset(Window window) => window.OffScreenMargin;

    public void ToggleMaximize() =>
        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximizeGlyph() =>
        _maximizeGlyph.Text = _window.WindowState == WindowState.Maximized ? RestoreGlyph : MaximizeGlyph;

    /// <summary>
    /// A caption button that acts on its own click (issue #254): the decoration roles alone left the clicks to Windows,
    /// which did nothing with them.
    /// </summary>
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
        WindowDecorationProperties.SetElementRole(button, WindowDecorationsElementRole.User);
        button.PointerEntered += (_, _) => SetHover(button, true, isClose);
        button.PointerExited += (_, _) => SetHover(button, false, isClose);
        button.PointerPressed += (_, e) => e.Handled = true;
        button.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            if (e.InitialPressMouseButton == MouseButton.Left && new Rect(button.Bounds.Size).Contains(e.GetPosition(button)))
            {
                click();
            }
        };
        return button;
    }

    /// <summary>The feedback icon (codicon), with the caption buttons' hover.</summary>
    private Border FeedbackButton()
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = Codicon.GeometryOf(Codicon.Feedback),
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(Shape.FillProperty, "Habi.TitleBarForeground");
        var button = new Border { Width = 40, Background = Brushes.Transparent, Child = icon };
        ToolTip.SetTip(button, "Send feedback");
        WindowDecorationProperties.SetElementRole(button, WindowDecorationsElementRole.User);
        button.PointerEntered += (_, _) => button.SetResourceReference(Border.BackgroundProperty, "Habi.TitleBarHover");
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        button.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            FeedbackRequested?.Invoke();
        };
        return button;
    }

    /// <summary>
    /// Left of the feedback button while the zoom is not 100% (issue #239), as a browser shows its zoom in the address
    /// bar; a click goes back to 100%.
    /// </summary>
    private static Border ZoomButton()
    {
        var text = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Habi.TitleBarForeground");
        var pill = new Border
        {
            Child = text,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(0, 5, 4, 5),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
        };
        pill.SetResourceReference(Border.BorderBrushProperty, "Habi.TitleBarHover");
        WindowDecorationProperties.SetElementRole(pill, WindowDecorationsElementRole.User);
        pill.PointerEntered += (_, _) => pill.SetResourceReference(Border.BackgroundProperty, "Habi.TitleBarHover");
        pill.PointerExited += (_, _) => pill.Background = Brushes.Transparent;
        pill.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            UiZoom.Reset();
        };

        void Show()
        {
            var percent = Core.Settings.ZoomSteps.Percent(UiZoom.Factor);
            text.Text = percent;
            ToolTip.SetTip(pill, $"Zoom {percent}. Click for 100% (Ctrl+Alt+0)");
            pill.IsVisible = !(Math.Abs(UiZoom.Factor - 1) < 0.0005);
        }

        pill.Background = Brushes.Transparent;
        Show();
        UiZoom.Changed += Show;
        return pill;
    }

    private static void SetHover(Border button, bool hover, bool isClose)
    {
        var text = (TextBlock)button.Child!;
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
            button.SetResourceReference(Border.BackgroundProperty, "Habi.TitleBarHover");
        }
    }

    private static Avalonia.Media.Imaging.Bitmap? LoadIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://habi/Assets/habi.ico"));
            return new WindowIcon(stream) is var windowIcon ? IconBitmap(windowIcon) : null;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The icon file's 32 px frame as a bitmap for the 16 px image (crisp at 200%).</summary>
    private static Avalonia.Media.Imaging.Bitmap? IconBitmap(WindowIcon icon)
    {
        using var stream = new MemoryStream();
        icon.Save(stream);
        stream.Position = 0;
        return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 32);
    }
}
