using Microsoft.Win32;

namespace HabilisTerminal.Themes;

/// <summary>Windows light/dark mode, high contrast and accent colour (the theme source chosen in SPEC §10).</summary>
public static class WindowsAppearance
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    public static bool AppsUseLightTheme =>
        Registry.GetValue($@"HKEY_CURRENT_USER\{PersonalizeKey}", "AppsUseLightTheme", 0) is int value && value != 0;

    /// <summary>The accent colour, or null when it cannot be read.</summary>
    public static ThemeColor? AccentColor
    {
        get
        {
            // AccentColor is stored as 0xAABBGGRR.
            if (Registry.GetValue($@"HKEY_CURRENT_USER\{DwmKey}", "AccentColor", null) is int abgr)
            {
                var value = unchecked((uint)abgr);
                return new ThemeColor((byte)value, (byte)(value >> 8), (byte)(value >> 16));
            }

            return null;
        }
    }

    /// <summary>The built-in theme matching the current Windows mode.</summary>
    public static string BuiltInThemeFor(bool highContrast) =>
        highContrast
            ? (AppsUseLightTheme ? ThemeLoader.HighContrastLight : ThemeLoader.HighContrastDark)
            : (AppsUseLightTheme ? ThemeLoader.LightModern : ThemeLoader.DarkModern);

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
