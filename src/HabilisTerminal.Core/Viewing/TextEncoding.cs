using System.Text;

namespace HabilisTerminal.Core.Viewing;

/// <summary>An encoding the viewer and editor can use, with how it was found (SPEC §9).</summary>
public sealed record TextEncoding(string Name, Encoding Encoding, int UnitSize, bool BigEndian, int BomLength, bool DetectedBom = false)
{
    public static TextEncoding Utf8 { get; } = new("UTF-8", new UTF8Encoding(false), 1, false, 3);

    public static TextEncoding Utf16LittleEndian { get; } = new("UTF-16 LE", new UnicodeEncoding(false, false), 2, false, 2);

    public static TextEncoding Utf16BigEndian { get; } = new("UTF-16 BE", new UnicodeEncoding(true, false), 2, true, 2);

    /// <summary>The system ANSI code page (GetACP; 1251 on Russian Windows), as Far uses it.</summary>
    public static TextEncoding Ansi { get; } = CodePage((int)GetACP(), "ANSI");

    /// <summary>The system OEM (DOS) code page (GetOEMCP; 866 on Russian Windows).</summary>
    public static TextEncoding Oem { get; } = CodePage((int)GetOEMCP(), "OEM");

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetACP();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>Encodings offered by Shift+F8 (Far order: UTF-8, ANSI, OEM, UTF-16 LE / BE, Windows-1251, CP866).</summary>
    public static IReadOnlyList<TextEncoding> Choices { get; } =
    [
        Utf8, Ansi, Oem, Utf16LittleEndian, Utf16BigEndian, CodePage(1251, "Windows-1251"), CodePage(866, "CP866"),
        CodePage(1252, "Windows-1252"), CodePage(20866, "KOI8-R"),
    ];

    public static TextEncoding CodePage(int codePage, string label)
    {
        // Code pages other than UTF-* need the provider; registering twice is harmless.
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new($"{label} ({codePage})", System.Text.Encoding.GetEncoding(codePage), 1, false, 0);
    }

    /// <summary>
    /// BOM first; then UTF-16 without BOM (many zero bytes in one column); then valid UTF-8 with non-ASCII bytes;
    /// otherwise the ANSI code page. Pure ASCII is reported as UTF-8.
    /// </summary>
    public static TextEncoding Detect(ReadOnlySpan<byte> sample)
    {
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Utf8 with { DetectedBom = true };
        }

        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Utf16LittleEndian with { DetectedBom = true };
        }

        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Utf16BigEndian with { DetectedBom = true };
        }

        if (sample.Length >= 4)
        {
            int evenZeros = 0, oddZeros = 0;
            for (var i = 0; i + 1 < sample.Length; i += 2)
            {
                evenZeros += sample[i] == 0 ? 1 : 0;
                oddZeros += sample[i + 1] == 0 ? 1 : 0;
            }

            var pairs = sample.Length / 2;
            if (oddZeros > pairs * 0.3 && evenZeros < pairs * 0.05 && MostlyPrintable(sample, 0))
            {
                return Utf16LittleEndian;
            }

            if (evenZeros > pairs * 0.3 && oddZeros < pairs * 0.05 && MostlyPrintable(sample, 1))
            {
                return Utf16BigEndian;
            }
        }

        return IsUtf8(sample) ? Utf8 : Ansi;
    }

    /// <summary>The low bytes of UTF-16 text are mostly printable or line breaks / tabs.</summary>
    private static bool MostlyPrintable(ReadOnlySpan<byte> sample, int start)
    {
        int printable = 0, total = 0;
        for (var i = start; i < sample.Length; i += 2)
        {
            total++;
            var b = sample[i];
            if (b >= 0x20 || b is 9 or 10 or 13)
            {
                printable++;
            }
        }

        return total > 0 && printable >= total * 0.95;
    }

    /// <summary>
    /// True when the bytes are valid UTF-8 (a truncated character at the very end is allowed: samples are cut).
    /// </summary>
    public static bool IsUtf8(ReadOnlySpan<byte> bytes)
    {
        var i = 0;
        while (i < bytes.Length)
        {
            var b = bytes[i];
            var length = b < 0x80 ? 1 : (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : (b & 0xF8) == 0xF0 ? 4 : 0;
            if (length == 0 || (length == 2 && b < 0xC2))
            {
                return false;
            }

            for (var k = 1; k < length; k++)
            {
                if (i + k >= bytes.Length)
                {
                    return true;
                }

                if ((bytes[i + k] & 0xC0) != 0x80)
                {
                    return false;
                }
            }

            i += length;
        }

        return true;
    }

    /// <summary>
    /// Text or binary (F4 decides between the built-in editor and the system editor): a BOM means text; otherwise
    /// no NUL bytes and few control characters in the sample.
    /// </summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> sample)
    {
        if (sample.Length == 0 || sample.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ||
            sample.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || sample.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return true;
        }

        var detected = Detect(sample);
        if (detected.UnitSize == 2)
        {
            return true;
        }

        var control = 0;
        foreach (var b in sample)
        {
            if (b == 0)
            {
                return false;
            }

            if (b < 0x20 && b is not (9 or 10 or 13 or 12 or 27))
            {
                control++;
            }
        }

        return control <= sample.Length / 100;
    }
}
