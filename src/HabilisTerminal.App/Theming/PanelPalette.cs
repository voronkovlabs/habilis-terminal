using System.Windows;
using System.Windows.Media;
using HabilisTerminal.Themes;

namespace HabilisTerminal.App.Theming;

/// <summary>
/// Brushes for file panels and widgets, taken from VS Code workbench keys of the current theme (SPEC §10).
/// Code reads the static properties at render time; XAML uses the same values as dynamic resources
/// (<c>Habi.*</c> keys), so a theme change repaints everything.
/// </summary>
internal static class PanelPalette
{
    static PanelPalette() => Apply(ThemeLoader.LoadBuiltIn(ThemeLoader.DarkModern));

    public static Brush Background { get; private set; } = null!;           // sideBar.background

    public static Brush Foreground { get; private set; } = null!;           // sideBar.foreground

    public static Brush DescriptionForeground { get; private set; } = null!; // descriptionForeground

    public static Brush Border { get; private set; } = null!;               // sideBar.border

    public static Brush HeaderBackground { get; private set; } = null!;     // sideBarSectionHeader.background

    public static Brush ActiveSelectionBackground { get; private set; } = null!; // list.activeSelectionBackground

    public static Brush ActiveSelectionForeground { get; private set; } = null!; // list.activeSelectionForeground

    public static Brush InactiveSelectionBackground { get; private set; } = null!; // list.inactiveSelectionBackground

    public static Brush FocusOutline { get; private set; } = null!;         // list.focusOutline

    public static Brush HoverBackground { get; private set; } = null!;      // list.hoverBackground

    public static Brush MarkedForeground { get; private set; } = null!;     // gitDecoration.modifiedResourceForeground

    public static Brush FolderIcon { get; private set; } = null!;           // icon.foreground

    public static Brush FileIcon { get; private set; } = null!;

    public static Brush ErrorForeground { get; private set; } = null!;      // errorForeground

    public static Brush InputBackground { get; private set; } = null!;      // input.background

    public static Brush WidgetBackground { get; private set; } = null!;     // quickInput.background

    public static Brush WidgetBorder { get; private set; } = null!;         // widget.border

    private static Brush[] s_tones = [];

    /// <summary>
    /// A plugin row tone (the SDK's RowTone: 1 Accent, 2 Good, 3 Warning, 4 Bad, 5 Dim) as a theme brush; null for
    /// normal rows (issue #49).
    /// </summary>
    public static Brush? Tone(int tone) => tone > 0 && tone < s_tones.Length ? s_tones[tone] : null;

    public static bool IsLight { get; private set; }

    public static void Apply(ColorTheme theme)
    {
        IsLight = theme.IsLight;
        var background = theme.GetOpaque("sideBar.background");
        Color Opaque(string key) => ToColor((theme.Get(key) ?? background).Over(background));

        // Scroll bar sliders keep the theme's alpha: they are drawn over the panel, page or terminal background alike.
        Color Translucent(string key) => theme.Get(key) is { } color ? ToColor(color) : Opaque("descriptionForeground");

        var border = Opaque("sideBar.border");
        if (ToColor(background) == border)
        {
            // Themes without a side bar border (Light Modern): use the panel border so panels stay apart.
            border = Opaque("panel.border");
        }

        Background = Freeze(ToColor(background));
        Foreground = Freeze(Opaque("sideBar.foreground"));
        DescriptionForeground = Freeze(Opaque("descriptionForeground"));
        Border = Freeze(border);
        HeaderBackground = Freeze(Opaque("sideBarSectionHeader.background"));
        ActiveSelectionBackground = Freeze(Opaque("list.activeSelectionBackground"));
        ActiveSelectionForeground = Freeze(Opaque("list.activeSelectionForeground"));
        InactiveSelectionBackground = Freeze(Opaque("list.inactiveSelectionBackground"));
        FocusOutline = Freeze(Opaque("list.focusOutline"));
        HoverBackground = Freeze(Opaque("list.hoverBackground"));
        MarkedForeground = Freeze(Opaque("gitDecoration.modifiedResourceForeground"));
        FolderIcon = Freeze(Opaque("icon.foreground"));
        FileIcon = DescriptionForeground;
        ErrorForeground = Freeze(Opaque("errorForeground"));
        InputBackground = Freeze(Opaque("input.background"));
        WidgetBackground = Freeze(Opaque("quickInput.background"));
        WidgetBorder = Freeze(Opaque("widget.border"));
        s_tones =
        [
            Foreground,
            Freeze(Opaque("list.highlightForeground")),
            Freeze(Opaque("terminal.ansiGreen")),
            Freeze(Opaque("terminal.ansiYellow")),
            ErrorForeground,
            DescriptionForeground,
        ];

        if (Application.Current is { } application)
        {
            var resources = application.Resources;
            resources["Habi.Foreground"] = Foreground;
            resources["Habi.Description"] = DescriptionForeground;
            resources["Habi.Selection"] = ActiveSelectionBackground;
            resources["Habi.SelectionForeground"] = ActiveSelectionForeground;
            resources["Habi.Hover"] = HoverBackground;
            resources["Habi.Accent"] = FocusOutline;
            resources["Habi.WidgetBackground"] = WidgetBackground;
            resources["Habi.WidgetBorder"] = WidgetBorder;
            resources["Habi.InputBackground"] = InputBackground;
            resources["Habi.Placeholder"] = Freeze(Opaque("input.placeholderForeground"));
            resources["Habi.Progress"] = Freeze(Opaque("progressBar.background"));
            resources["Habi.ProgressTrack"] = Freeze(Opaque("scrollbarSlider.background"));
            resources["Habi.ScrollbarSlider"] = Freeze(Translucent("scrollbarSlider.background"));
            resources["Habi.ScrollbarSliderHover"] = Freeze(Translucent("scrollbarSlider.hoverBackground"));
            resources["Habi.ScrollbarSliderActive"] = Freeze(Translucent("scrollbarSlider.activeBackground"));
            resources["Habi.InputBorder"] = Freeze(Opaque("input.border") == ToColor(theme.GetOpaque("input.background").Over(background)) ? Opaque("widget.border") : Opaque("input.border"));
            resources["Habi.ButtonBackground"] = Freeze(Opaque("button.background"));
            resources["Habi.ButtonForeground"] = Freeze(Opaque("button.foreground"));
            resources["Habi.ButtonBorder"] = Freeze(Opaque("button.border"));
            resources["Habi.SecondaryButtonBackground"] = Freeze(Opaque("button.secondaryBackground"));
            resources["Habi.SecondaryButtonForeground"] = Freeze(Opaque("button.secondaryForeground"));
            resources["Habi.Error"] = ErrorForeground;
            resources["Habi.Background"] = Background;
            // For plugins' own WPF views (issue #78, HabilisTerminal.Plugins.Wpf.HostBrushes).
            resources["Habi.Border"] = Border;
            resources["Habi.ToneAccent"] = s_tones[1];
            resources["Habi.ToneGood"] = s_tones[2];
            resources["Habi.ToneWarning"] = s_tones[3];
            resources["Habi.ToneBad"] = s_tones[4];
            resources["Habi.ToneDim"] = s_tones[5];
            resources["Habi.TitleBarBackground"] = Freeze(Opaque("titleBar.activeBackground"));

            // A theme may set only the background: the menu must stay readable on it (issue #109).
            var titleForeground = Readable(Opaque("titleBar.activeForeground"), Opaque("titleBar.activeBackground"));
            var titleBackground = Opaque("titleBar.activeBackground");
            resources["Habi.TitleBarForeground"] = Freeze(titleForeground);

            // The title bar's own colours: a list's hover colour can hide the text (the Far theme's dark blue).
            resources["Habi.TitleBarHover"] = Freeze(Mix(titleBackground, titleForeground, 0.18));
        }
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    /// <summary>The colour itself when it can be read on the background, else black or white, whichever reads better.</summary>
    private static Color Readable(Color color, Color background)
    {
        double Contrast(Color a)
        {
            var (x, y) = (Luminance(a), Luminance(background));
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }

        return Contrast(color) >= 3 ? color : Contrast(Colors.White) >= Contrast(Colors.Black) ? Colors.White : Colors.Black;
    }

    private static Color Mix(Color from, Color to, double share) => Color.FromRgb(
        (byte)(from.R + ((to.R - from.R) * share)),
        (byte)(from.G + ((to.G - from.G) * share)),
        (byte)(from.B + ((to.B - from.B) * share)));

    public static Color ToColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
