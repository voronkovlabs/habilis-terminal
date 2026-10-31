using System.Windows.Media;
using Microsoft.Terminal.Wpf;

namespace TerminalCommander.App.Theming;

/// <summary>
/// Hard-coded Dark Modern colours for the walking skeleton.
/// Replaced by the VS Code theme loader in stage 5.
/// </summary>
internal static class PlaceholderTheme
{
    public static readonly Color SideBarBackground = Rgb(0x181818);
    public static readonly Color SideBarForeground = Rgb(0xCCCCCC);
    public static readonly Color SideBarBorder = Rgb(0x2B2B2B);
    public static readonly Color FocusBorder = Rgb(0x0078D4);
    public static readonly Color DescriptionForeground = Rgb(0x9D9D9D);
    public static readonly Color TerminalBackground = Rgb(0x181818);
    public static readonly Color TerminalForeground = Rgb(0xCCCCCC);
    public static readonly Color TerminalSelection = Rgb(0x264F78);

    // VS Code default dark terminal palette (terminal.ansiBlack … terminal.ansiBrightWhite).
    private static readonly int[] Ansi =
    [
        0x000000, 0xCD3131, 0x0DBC79, 0xE5E510, 0x2472C8, 0xBC3FBC, 0x11A8CD, 0xE5E5E5,
        0x666666, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xE5E5E5,
    ];

    public static TerminalTheme Terminal => new()
    {
        DefaultBackground = ColorRef(TerminalBackground),
        DefaultForeground = ColorRef(TerminalForeground),
        DefaultSelectionBackground = ColorRef(TerminalSelection),
        CursorStyle = CursorStyle.BlinkingBar,
        ColorTable = Ansi.Select(c => ColorRef(Rgb(c))).ToArray(),
    };

    public static Color Background => TerminalBackground;

    private static uint ColorRef(Color c) => (uint)(c.R | (c.G << 8) | (c.B << 16));

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
