namespace HabilisTerminal.Terminal;

/// <summary>
/// The lines of one screen (main or alternate). The last <see cref="Rows"/> lines are the screen; the main screen keeps
/// the lines that scrolled off its top before them.
/// </summary>
internal sealed class ScreenBuffer(int columns, int rows, int scrollbackLimit)
{
    private const int TrimBatch = 256;

    private readonly List<TermLine> _lines = Enumerable.Range(0, rows).Select(_ => new TermLine(columns, CellStyle.Plain)).ToList();

    public int Columns { get; private set; } = columns;

    public int Rows { get; private set; } = rows;

    public int ScrollbackLimit { get; } = scrollbackLimit;

    /// <summary>
    /// A taller screen takes its new top rows back from the scrollback, so the text stays at the bottom, as terminals do
    /// (issue #312). Off under ConPTY, which repaints the whole screen after a resize itself.
    /// </summary>
    public bool GrowIntoScrollback { get; set; }

    public int Count => _lines.Count;

    public int ScrollbackCount => _lines.Count - Rows;

    /// <summary>Lines dropped from the top since the start: lets a view keep its place in the scrollback.</summary>
    public long Dropped { get; private set; }

    /// <summary>A line of the screen, 0 at the top.</summary>
    public TermLine Row(int row) => _lines[_lines.Count - Rows + row];

    /// <summary>A line counted from the oldest scrollback line.</summary>
    public TermLine Line(int index) => _lines[index];

    /// <summary>Scrolls the lines from <paramref name="top"/> to <paramref name="bottom"/> up by one.</summary>
    public void ScrollUp(int top, int bottom, CellStyle style, bool toScrollback = true)
    {
        if (toScrollback && top == 0 && bottom == Rows - 1 && ScrollbackLimit > 0)
        {
            // The whole screen: the top line goes to the scrollback.
            _lines.Add(new TermLine(Columns, style));
            if (_lines.Count - Rows > ScrollbackLimit + TrimBatch)
            {
                var drop = _lines.Count - Rows - ScrollbackLimit;
                _lines.RemoveRange(0, drop);
                Dropped += drop;
            }

            return;
        }

        var start = _lines.Count - Rows;
        var line = _lines[start + top];
        _lines.RemoveAt(start + top);
        line.Clear(style);
        line.Wrapped = false;
        _lines.Insert(start + bottom, line);
    }

    /// <summary>Scrolls the lines from <paramref name="top"/> to <paramref name="bottom"/> down by one.</summary>
    public void ScrollDown(int top, int bottom, CellStyle style)
    {
        var start = _lines.Count - Rows;
        var line = _lines[start + bottom];
        _lines.RemoveAt(start + bottom);
        line.Clear(style);
        line.Wrapped = false;
        _lines.Insert(start + top, line);
    }

    /// <summary>Clears the scrollback (ED 3).</summary>
    public void ClearScrollback()
    {
        var drop = ScrollbackCount;
        _lines.RemoveRange(0, drop);
        Dropped += drop;
    }

    /// <summary>
    /// New size. Lines keep their text, cut or padded. Fewer rows drop blank lines under the cursor first and send the
    /// rest to the scrollback; more rows add blank lines at the bottom. Returns the cursor's new row.
    /// </summary>
    public int Resize(int columns, int rows, int cursorRow)
    {
        foreach (var line in _lines)
        {
            line.Resize(columns, CellStyle.Plain);
        }

        Columns = columns;
        while (rows < Rows)
        {
            var last = _lines[^1];
            if (cursorRow < Rows - 1 && last.Text().Length == 0)
            {
                _lines.RemoveAt(_lines.Count - 1);
            }
            else
            {
                cursorRow--;
            }

            Rows--;
        }

        while (rows > Rows)
        {
            if (GrowIntoScrollback && _lines.Count > Rows)
            {
                cursorRow++;
            }
            else
            {
                _lines.Add(new TermLine(columns, CellStyle.Plain));
            }

            Rows++;
        }

        if (ScrollbackLimit == 0 && _lines.Count > Rows)
        {
            _lines.RemoveRange(0, _lines.Count - Rows);
        }

        return Math.Clamp(cursorRow, 0, Rows - 1);
    }
}
