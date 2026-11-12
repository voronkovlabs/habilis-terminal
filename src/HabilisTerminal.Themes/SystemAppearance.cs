namespace HabilisTerminal.Themes;

/// <summary>The system's light/dark mode, high contrast and accent colour (the theme source chosen in SPEC §10).</summary>
public interface ISystemAppearance
{
    bool AppsUseLightTheme { get; }

    /// <summary>The accent colour, or null when it cannot be read.</summary>
    ThemeColor? AccentColor { get; }

    bool IsHighContrast();
}

/// <summary>Dark, no accent, no high contrast: until the UI layer tells better (#259).</summary>
public sealed class DefaultSystemAppearance : ISystemAppearance
{
    public bool AppsUseLightTheme => false;

    public ThemeColor? AccentColor => null;

    public bool IsHighContrast() => false;
}

/// <summary>Picks the theme that follows the system, and tints it with the accent colour.</summary>
public static class SystemAppearance
{
    /// <summary>The system's settings; the app replaces it on systems where the UI framework reads them.</summary>
    public static ISystemAppearance Current { get; set; } =
        OperatingSystem.IsWindows() ? new WindowsSystemAppearance() : new DefaultSystemAppearance();

    public static bool AppsUseLightTheme => Current.AppsUseLightTheme;

    public static ThemeColor? AccentColor => Current.AccentColor;

    public static bool IsHighContrast() => Current.IsHighContrast();

    /// <summary>The built-in theme matching the current Windows mode.</summary>
    public static string BuiltInThemeFor(bool highContrast) =>
        highContrast
            ? (Current.AppsUseLightTheme ? ThemeLoader.HighContrastLight : ThemeLoader.HighContrastDark)
            : (Current.AppsUseLightTheme ? ThemeLoader.LightModern : ThemeLoader.DarkModern);

    /// <summary>
    /// Tints focus and selection with the accent colour: focus outlines and progress use it directly, the active
    /// selection uses it blended over the side bar, so text stays readable in light and dark themes.
    /// </summary>
    public static ColorTheme ApplyAccent(ColorTheme theme, ThemeColor accent)
    {
        if (theme.Type is ThemeType.HighContrast or ThemeType.HighContrastLight)
        {
            return theme;
        }

        var sideBar = theme.GetOpaque("sideBar.background");
        var selection = accent.WithAlpha(theme.IsLight ? 0.85 : 0.55).Over(sideBar);
        var selectionForeground = selection.Luminance > 0.45 ? ThemeColor.FromRgb(0x000000) : ThemeColor.FromRgb(0xFFFFFF);
        return theme.With(new Dictionary<string, ThemeColor>
        {
            ["focusBorder"] = accent,
            ["list.focusOutline"] = accent,
            ["progressBar.background"] = accent,
            ["list.activeSelectionBackground"] = selection,
            ["list.activeSelectionForeground"] = selectionForeground,
        });
    }
}
