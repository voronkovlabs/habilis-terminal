using System.Globalization;

namespace HabilisTerminal.Themes;

/// <summary>An sRGB colour with alpha, parsed from VS Code theme notation (<c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c>, <c>#RRGGBBAA</c>).</summary>
public readonly record struct ThemeColor(byte R, byte G, byte B, byte A = 255)
{
    public static ThemeColor FromRgb(int rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static bool TryParse(string? text, out ThemeColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text) || text[0] != '#')
        {
            return false;
        }

        var hex = text.AsSpan(1).Trim();
        if (hex.Length is 3 or 4)
        {
            Span<char> expanded = stackalloc char[hex.Length * 2];
            for (var i = 0; i < hex.Length; i++)
            {
                expanded[2 * i] = expanded[(2 * i) + 1] = hex[i];
            }

            return TryParseHex(expanded, out color);
        }

        return hex.Length is 6 or 8 && TryParseHex(hex, out color);
    }

    public static ThemeColor Parse(string text) =>
        TryParse(text, out var color) ? color : throw new FormatException($"Invalid colour '{text}'.");

    /// <summary>Draws this colour (with its alpha) over an opaque background: the colour a user actually sees.</summary>
    public ThemeColor Over(ThemeColor background)
    {
        var alpha = A / 255.0;
        return new ThemeColor(
            (byte)Math.Round((R * alpha) + (background.R * (1 - alpha))),
            (byte)Math.Round((G * alpha) + (background.G * (1 - alpha))),
            (byte)Math.Round((B * alpha) + (background.B * (1 - alpha))));
    }

    public ThemeColor WithAlpha(double alpha) => this with { A = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255) };

    /// <summary>Relative luminance (WCAG), 0 = black, 1 = white.</summary>
    public double Luminance
    {
        get
        {
            static double Channel(byte c)
            {
                var v = c / 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Channel(R)) + (0.7152 * Channel(G)) + (0.0722 * Channel(B));
        }
    }

    public override string ToString() =>
        A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    private static bool TryParseHex(ReadOnlySpan<char> hex, out ThemeColor color)
    {
        color = default;
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        color = hex.Length == 6
            ? new ThemeColor((byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : new ThemeColor((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }
}
