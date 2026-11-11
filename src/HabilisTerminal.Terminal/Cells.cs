namespace HabilisTerminal.Terminal;

/// <summary>A cell colour: the default, one of the 256 palette entries, or 24-bit RGB.</summary>
public readonly record struct TermColor(uint Value)
{
    private const uint PaletteTag = 0x0100_0000;
    private const uint RgbTag = 0x0200_0000;

    public static readonly TermColor Default = new(0);

    public static TermColor Palette(int index) => new(PaletteTag | (uint)(index & 0xFF));

    public static TermColor Rgb(byte r, byte g, byte b) => new(RgbTag | ((uint)r << 16) | ((uint)g << 8) | b);

    public bool IsDefault => Value == 0;

    public bool IsPalette => (Value & PaletteTag) != 0;

    public bool IsRgb => (Value & RgbTag) != 0;

    public int Index => (int)(Value & 0xFF);

    public byte R => (byte)(Value >> 16);

    public byte G => (byte)(Value >> 8);

    public byte B => (byte)Value;
}

[Flags]
public enum TextAttributes : byte
{
    None = 0,
    Bold = 1,
    Faint = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
    Hidden = 32,
    Strike = 64,
}

/// <summary>What a cell looks like besides its text.</summary>
public readonly record struct CellStyle(TermColor Foreground, TermColor Background, TextAttributes Flags)
{
    public static readonly CellStyle Plain = new(TermColor.Default, TermColor.Default, TextAttributes.None);

    /// <summary>What erasing leaves: the current background only (background colour erase).</summary>
    public CellStyle Erased => new(TermColor.Default, Background, TextAttributes.None);
}

/// <summary>
/// One screen cell. <see cref="Text"/> is null for a blank, a grapheme otherwise, and empty for the right half of a
/// wide character.
/// </summary>
public readonly record struct Cell(string? Text, CellStyle Style)
{
    public bool IsBlank => Text is null;

    public bool IsWideTail => Text is { Length: 0 };

    public static Cell Blank(CellStyle style) => new(null, style);
}

/// <summary>A screen or scrollback line.</summary>
public sealed class TermLine
{
    public TermLine(int columns, CellStyle style)
    {
        Cells = new Cell[columns];
        Clear(style);
    }

    public Cell[] Cells { get; private set; }

    /// <summary>The text went on to the next line by itself (a soft wrap): copying joins them.</summary>
    public bool Wrapped { get; set; }

    public void Clear(CellStyle style) => Fill(0, Cells.Length, style);

    public void Fill(int from, int to, CellStyle style)
    {
        from = Math.Max(0, from);
        to = Math.Min(Cells.Length, to);
        for (var i = from; i < to; i++)
        {
            Cells[i] = Cell.Blank(style);
        }
    }

    public void Resize(int columns, CellStyle style)
    {
        if (columns == Cells.Length)
        {
            return;
        }

        var old = Cells;
        Cells = new Cell[columns];
        Array.Copy(old, Cells, Math.Min(old.Length, columns));
        for (var i = old.Length; i < columns; i++)
        {
            Cells[i] = Cell.Blank(style);
        }

        // A wide character cut in half by the new width is gone.
        if (columns > 0 && columns < old.Length && old[columns].IsWideTail)
        {
            Cells[columns - 1] = Cell.Blank(style);
        }
    }

    /// <summary>The line's text, blanks as spaces, trailing blanks dropped.</summary>
    public string Text(int from = 0, int to = int.MaxValue)
    {
        to = Math.Min(to, Cells.Length);
        var builder = new System.Text.StringBuilder();
        var spaces = 0;
        for (var i = Math.Max(0, from); i < to; i++)
        {
            var cell = Cells[i];
            if (cell.IsWideTail)
            {
                continue;
            }

            if (cell.Text is null || cell.Text == " ")
            {
                spaces++;
                continue;
            }

            builder.Append(' ', spaces);
            spaces = 0;
            builder.Append(cell.Text);
        }

        return builder.ToString();
    }
}
