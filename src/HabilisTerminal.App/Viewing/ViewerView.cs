using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HabilisTerminal.App.Theming;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.App.Viewing;

/// <summary>
/// Draws a <see cref="ViewerDocument"/> as text (optionally wrapped) or hex, from a byte offset (SPEC §9).
/// Used by the F3 page and by quick view; only the visible rows are decoded.
/// </summary>
public sealed class ViewerView : FrameworkElement
{
    public const int HexBytesPerRow = 16;
    private static double FontSize = 13;
    private static int FontVersion;
    private const double Padding = 8;
    private const int TabSize = 8;

    private static Typeface Mono = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private int _cellFontVersion;
    private ViewerDocument? _document;
    private Size _cell;

    public ViewerView()
    {
        Focusable = false;
        ClipToBounds = true;
    }

    /// <summary>Raised after the top position changes (for the page header).</summary>
    public event Action? PositionChanged;

    public ViewerDocument? Document
    {
        get => _document;
        set
        {
            _document = value;
            TopOffset = value?.ContentStart ?? 0;
            LeftColumn = 0;
            Highlight = null;
            InvalidateVisual();
            PositionChanged?.Invoke();
        }
    }

    public long TopOffset { get; private set; }

    public bool Wrap { get; set; }

    /// <summary>The <c>viewer.fontFamily</c> / <c>viewer.fontSize</c> settings (also used by quick view).</summary>
    public static void SetFont(string family, double size)
    {
        Mono = new Typeface(new FontFamily(string.IsNullOrWhiteSpace(family) ? "Cascadia Mono, Consolas" : family + ", Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FontSize = size;
        FontVersion++;
    }

    public bool Hex { get; set; }

    public int LeftColumn { get; private set; }

    /// <summary>A search hit to highlight: byte offset and length in characters.</summary>
    public (long Offset, int Length)? Highlight { get; set; }

    public int VisibleRows => Math.Max(1, (int)((ActualHeight - (2 * Padding)) / Cell.Height));

    public int VisibleColumns => Math.Max(1, (int)((ActualWidth - (2 * Padding)) / Cell.Width));

    /// <summary>Percent of the file above the top row.</summary>
    public double Percent => _document is { Length: > 0 } d ? Math.Min(100, TopOffset * 100.0 / d.Length) : 100;

    private Size Cell
    {
        get
        {
            if (_cell.IsEmpty || _cell.Width == 0 || _cellFontVersion != FontVersion)
            {
                _cellFontVersion = FontVersion;
                var sample = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, FontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                _cell = new Size(sample.WidthIncludingTrailingWhitespace, Math.Ceiling(sample.Height));
            }

            return _cell;
        }
    }

    public void ScrollLines(int lines)
    {
        if (_document is null)
        {
            return;
        }

        var offset = TopOffset;
        for (var i = 0; i < Math.Abs(lines); i++)
        {
            var next = lines > 0 ? Next(offset) : Previous(offset);
            if (lines > 0 && next >= _document.Length)
            {
                break;
            }

            offset = next;
        }

        SetTop(offset);
    }

    public void ScrollPages(int pages) => ScrollLines(pages * Math.Max(1, VisibleRows - 1));

    public void GoToStart() => SetTop(_document?.ContentStart ?? 0);

    /// <summary>The last screenful (Ctrl+End).</summary>
    public void GoToEnd()
    {
        if (_document is null)
        {
            return;
        }

        var offset = Hex ? Math.Max(0, (_document.Length - 1) / HexBytesPerRow * HexBytesPerRow) : _document.PreviousLineStart(_document.Length);
        for (var i = 1; i < VisibleRows && offset > _document.ContentStart; i++)
        {
            offset = Previous(offset);
        }

        SetTop(offset);
    }

    /// <summary>Shows the line containing <paramref name="offset"/> a few rows from the top.</summary>
    public void Reveal(long offset)
    {
        if (_document is null)
        {
            return;
        }

        var line = Hex ? offset / HexBytesPerRow * HexBytesPerRow : _document.LineStartOf(offset);
        for (var i = 0; i < 3 && line > _document.ContentStart; i++)
        {
            line = Previous(line);
        }

        SetTop(line);
    }

    public void ScrollHorizontally(int columns)
    {
        LeftColumn = Math.Max(0, LeftColumn + columns);
        InvalidateVisual();
    }

    public void Refresh()
    {
        if (_document is not null && Hex)
        {
            TopOffset = TopOffset / HexBytesPerRow * HexBytesPerRow;
        }
        else if (_document is not null)
        {
            TopOffset = _document.LineStartOf(TopOffset);
        }

        InvalidateVisual();
        PositionChanged?.Invoke();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _cell = Size.Empty;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollLines(e.Delta > 0 ? -3 : 3);
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        dc.DrawRectangle(PanelPalette.Background, null, new Rect(RenderSize));
        if (_document is null)
        {
            return;
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var cell = Cell;
        var rows = VisibleRows + 1;
        var offset = TopOffset;
        var y = Padding;
        var row = 0;
        while (row < rows && offset < _document.Length)
        {
            if (Hex)
            {
                DrawText(dc, HexRow(offset), Padding, y, PanelPalette.Foreground, pixelsPerDip);
                offset += HexBytesPerRow;
                y += cell.Height;
                row++;
                continue;
            }

            var lineStart = offset;
            var text = ExpandTabs(_document.ReadLine(offset, out var next));
            offset = next;
            // Only what fits on screen is drawn: a minified file is one line split into 16 KB pieces, and drawing
            // a whole piece per row made the viewer hang and WPF overlap the glyphs (issue #76).
            var segments = Wrap ? Chunk(text, VisibleColumns) : [Visible(text, LeftColumn, VisibleColumns + 1)];
            foreach (var segment in segments)
            {
                if (row >= rows)
                {
                    break;
                }

                if (Highlight is { } hit && hit.Offset >= lineStart && hit.Offset < next && segment.Length > 0)
                {
                    DrawHighlight(dc, lineStart, hit, y, cell);
                }

                DrawText(dc, segment, Padding, y, PanelPalette.Foreground, pixelsPerDip);
                y += cell.Height;
                row++;
            }
        }
    }

    private void DrawHighlight(DrawingContext dc, long lineStart, (long Offset, int Length) hit, double y, Size cell)
    {
        // Character position of the hit inside the line (decoded prefix length).
        var prefixBytes = (int)(hit.Offset - lineStart);
        var buffer = new byte[prefixBytes];
        _document!.Read(lineStart, buffer, prefixBytes);
        var column = ExpandTabs(_document.Encoding.Encoding.GetString(buffer)).Length - (Wrap ? 0 : LeftColumn);
        if (Wrap)
        {
            column %= VisibleColumns;
        }

        if (column < 0)
        {
            return;
        }

        dc.DrawRectangle(PanelPalette.ActiveSelectionBackground, null, new Rect(Padding + (column * cell.Width), y, hit.Length * cell.Width, cell.Height));
    }

    private string HexRow(long offset)
    {
        var bytes = new byte[HexBytesPerRow];
        var count = _document!.Read(offset, bytes, HexBytesPerRow);
        var line = new StringBuilder();
        line.Append(offset.ToString("X10", CultureInfo.InvariantCulture)).Append("  ");
        for (var i = 0; i < HexBytesPerRow; i++)
        {
            line.Append(i < count ? bytes[i].ToString("X2", CultureInfo.InvariantCulture) : "  ").Append(i == 7 ? "  " : " ");
        }

        line.Append(' ');
        var singleByte = _document.Encoding.UnitSize == 1 && _document.Encoding.Encoding is not UTF8Encoding ? _document.Encoding.Encoding : Encoding.Latin1;
        for (var i = 0; i < count; i++)
        {
            var c = singleByte.GetString(bytes, i, 1)[0];
            line.Append(char.IsControl(c) ? '.' : c);
        }

        return line.ToString();
    }

    private long Next(long offset) => Hex ? offset + HexBytesPerRow : _document!.NextLineStart(offset);

    private long Previous(long offset) => Hex ? Math.Max(0, offset - HexBytesPerRow) : _document!.PreviousLineStart(offset);

    private void SetTop(long offset)
    {
        TopOffset = Math.Clamp(offset, 0, Math.Max(0, _document?.Length ?? 0));
        InvalidateVisual();
        PositionChanged?.Invoke();
    }

    private static string ExpandTabs(string text)
    {
        if (!text.Contains('\t', StringComparison.Ordinal))
        {
            return text;
        }

        var result = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            if (c == '\t')
            {
                result.Append(' ', TabSize - (result.Length % TabSize));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    private static string Visible(string text, int start, int count) =>
        text.Length <= start ? string.Empty : text.Substring(start, Math.Min(count, text.Length - start));

    private static List<string> Chunk(string text, int width)
    {
        var chunks = new List<string>();
        for (var i = 0; i < text.Length; i += width)
        {
            chunks.Add(text.Substring(i, Math.Min(width, text.Length - i)));
        }

        if (chunks.Count == 0)
        {
            chunks.Add(string.Empty);
        }

        return chunks;
    }

    private static void DrawText(DrawingContext dc, string text, double x, double y, Brush brush, double pixelsPerDip)
    {
        if (text.Length == 0)
        {
            return;
        }

        // Control characters would draw as boxes in some fonts; show them as visible placeholders.
        var printable = string.Create(text.Length, text, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = source[i] < ' ' ? '·' : source[i];
            }
        });
        dc.DrawText(new FormattedText(printable, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Mono, FontSize, brush, pixelsPerDip), new Point(x, y));
    }
}
