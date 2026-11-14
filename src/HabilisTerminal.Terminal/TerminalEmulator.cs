using System.Globalization;
using System.Text;

namespace HabilisTerminal.Terminal;

public enum CursorShape
{
    Block,
    Underline,
    Bar,
}

/// <summary>
/// A VT terminal: text from a pseudo console goes in, a screen of cells comes out. What ConPTY and common console
/// programs send is covered: cursor movement, erasing, scrolling regions, SGR colours (16, 256, 24-bit), the alternate
/// screen, wide characters, line drawing, and the modes a console view needs to know (bracketed paste, win32-input-mode).
/// Thread-safe: <see cref="Feed"/> runs on the reader's thread, a view reads under <see cref="SyncRoot"/>.
/// </summary>
public sealed class TerminalEmulator : IVtHandler
{
    public const int DefaultScrollback = 10_000;

    private readonly VtParser _parser;
    private readonly ScreenBuffer _main;
    private readonly ScreenBuffer _alternate;
    private ScreenBuffer _screen;
    private CellStyle _style = CellStyle.Plain;
    private int _x;
    private int _y;
    private bool _pendingWrap;
    private int _top;
    private int _bottom;
    private bool[] _tabStops = [];
    private (int X, int Y, CellStyle Style, bool Origin, bool LineDrawing) _saved;
    private bool _lineDrawing;
    private bool _g1LineDrawing;
    private bool _shiftOut;
    private Rune? _lastPrinted;
    private readonly StringBuilder _replies = new();

    public TerminalEmulator(int columns, int rows, int scrollback = DefaultScrollback)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        _parser = new VtParser(this);
        _main = new ScreenBuffer(columns, rows, scrollback);
        _alternate = new ScreenBuffer(columns, rows, 0);
        _screen = _main;
        _bottom = rows - 1;
        ResetTabStops(columns);
    }

    /// <summary>Lock it while reading the screen from another thread.</summary>
    public object SyncRoot { get; } = new();

    public int Columns => _screen.Columns;

    public int Rows => _screen.Rows;

    public int CursorX => Math.Min(_x, Columns - 1);

    public int CursorY => _y;

    public bool CursorVisible { get; private set; } = true;

    public bool CursorBlinks { get; private set; } = true;

    public CursorShape CursorShape { get; private set; } = CursorShape.Block;

    public bool AlternateScreen => _screen == _alternate;

    public bool ApplicationCursorKeys { get; private set; }

    public bool ApplicationKeypad { get; private set; }

    public bool BracketedPaste { get; private set; }

    /// <summary>The other side asked for keys as win32-input-mode sequences (ConPTY does, DECSET 9001).</summary>
    public bool Win32InputMode { get; private set; }

    /// <summary>The other side tracks focus (DECSET 1004): send <c>ESC [ I</c> and <c>ESC [ O</c>.</summary>
    public bool FocusEvents { get; private set; }

    public bool AutoWrap { get; private set; } = true;

    public bool InsertMode { get; private set; }

    public bool OriginMode { get; private set; }

    public bool ReverseVideo { get; private set; }

    public string Title { get; private set; } = string.Empty;

    /// <summary>Lines above the screen (the main screen's scrollback; none on the alternate screen).</summary>
    public int ScrollbackCount => _screen.ScrollbackCount;

    /// <summary>Lines dropped from the oldest end of the scrollback so far.</summary>
    public long DroppedLines => _screen.Dropped;

    /// <summary>Changes since the start: a view redraws when it moved.</summary>
    public long Version { get; private set; }

    /// <summary>Something changed. Raised outside the lock, on the thread that fed the text.</summary>
    public event Action? Changed;

    /// <summary>Answers to the other side's queries (cursor position, device attributes): write them back.</summary>
    public event Action<string>? Reply;

    /// <summary>The bell rang.</summary>
    public event Action? Bell;

    public event Action<string>? TitleChanged;

    private (int Row, int Rows, int Columns)? _commandMark;

    /// <summary>
    /// When the screen grows taller, its new top rows come back from the scrollback and the text stays at the bottom
    /// (issue #312): for shells on a Unix pseudo terminal. ConPTY repaints the screen after a resize itself.
    /// </summary>
    public bool GrowIntoScrollback
    {
        get => _main.GrowIntoScrollback;
        set => _main.GrowIntoScrollback = value;
    }

    /// <summary>
    /// The cursor row and screen size where the last OSC 133;C (command accepted) was met, once: null when none came
    /// since the last call.
    /// </summary>
    public (int Row, int Rows, int Columns)? TakeCommandMark()
    {
        lock (SyncRoot)
        {
            var mark = _commandMark;
            _commandMark = null;
            return mark;
        }
    }

    /// <summary>A line of the screen, 0 at the top. Read under <see cref="SyncRoot"/>.</summary>
    public TermLine ScreenLine(int row) => _screen.Row(row);

    /// <summary>
    /// A line with scrollback: 0 is the oldest scrollback line, <see cref="ScrollbackCount"/> the top screen line.
    /// Read under <see cref="SyncRoot"/>.
    /// </summary>
    public TermLine Line(int index) => _screen.Line(index);

    public int LineCount => _screen.Count;

    public void Feed(string text) => Feed(text.AsSpan());

    public void Feed(ReadOnlySpan<char> text)
    {
        string? replies = null;
        string? title = null;
        var bell = false;
        lock (SyncRoot)
        {
            var oldTitle = Title;
            _bell = false;
            _parser.Feed(text);
            Version++;
            if (_replies.Length > 0)
            {
                replies = _replies.ToString();
                _replies.Clear();
            }

            if (!ReferenceEquals(oldTitle, Title) && oldTitle != Title)
            {
                title = Title;
            }

            bell = _bell;
        }

        if (replies is not null)
        {
            Reply?.Invoke(replies);
        }

        if (title is not null)
        {
            TitleChanged?.Invoke(title);
        }

        if (bell)
        {
            Bell?.Invoke();
        }

        Changed?.Invoke();
    }

    private bool _bell;

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        lock (SyncRoot)
        {
            if (columns == Columns && rows == Rows)
            {
                return;
            }

            var other = _screen == _main ? _alternate : _main;
            other.Resize(columns, rows, Math.Min(_y, other.Rows - 1));
            _y = _screen.Resize(columns, rows, _y);
            _x = Math.Min(_x, columns - 1);
            _pendingWrap = false;
            _top = 0;
            _bottom = rows - 1;
            ResetTabStops(columns);
            Version++;
        }

        Changed?.Invoke();
    }

    /// <summary>The text between two points (line index as in <see cref="Line"/>, column), soft wraps joined.</summary>
    public string TextBetween(int startLine, int startColumn, int endLine, int endColumn)
    {
        lock (SyncRoot)
        {
            var text = new StringBuilder();
            for (var i = Math.Max(0, startLine); i <= Math.Min(endLine, LineCount - 1); i++)
            {
                var line = Line(i);
                var from = i == startLine ? startColumn : 0;
                var to = i == endLine ? endColumn : int.MaxValue;
                text.Append(line.Text(from, to));
                if (i < endLine && !line.Wrapped)
                {
                    text.Append(Environment.NewLine);
                }
            }

            return text.ToString();
        }
    }

    // ---- The parser's actions ----

    void IVtHandler.Print(Rune rune)
    {
        if (_lineDrawing ^ (_shiftOut && _g1LineDrawing) && rune.Value is >= 0x5F and <= 0x7E)
        {
            rune = new Rune(LineDrawing[rune.Value - 0x5F]);
        }

        var width = CharWidth.Of(rune);
        if (width == 0)
        {
            AppendToPrevious(rune);
            return;
        }

        _lastPrinted = rune;
        if (_pendingWrap)
        {
            if (AutoWrap)
            {
                _screen.Row(_y).Wrapped = true;
                _x = 0;
                LineFeed();
            }

            _pendingWrap = false;
        }

        if (width == 2 && _x == Columns - 1)
        {
            if (!AutoWrap)
            {
                return;
            }

            _screen.Row(_y).Cells[_x] = Cell.Blank(_style.Erased);
            _screen.Row(_y).Wrapped = true;
            _x = 0;
            LineFeed();
        }

        if (width > Columns)
        {
            return;
        }

        var line = _screen.Row(_y);
        if (InsertMode)
        {
            InsertCells(width);
        }

        BreakWide(line, _x);
        if (width == 2)
        {
            BreakWide(line, _x + 1);
        }

        line.Cells[_x] = new Cell(rune.ToString(), _style);
        if (width == 2)
        {
            line.Cells[_x + 1] = new Cell(string.Empty, _style);
        }

        _x += width;
        if (_x >= Columns)
        {
            _x = Columns - 1;
            _pendingWrap = true;
        }
    }

    void IVtHandler.Execute(char control)
    {
        switch (control)
        {
            case '\u0007':
                _bell = true;
                break;
            case '\b':
                if (_pendingWrap)
                {
                    _pendingWrap = false;
                }
                else if (_x > 0)
                {
                    _x--;
                }

                break;
            case '\t':
                _x = NextTab(_x);
                _pendingWrap = false;
                break;
            case '\n' or '\v' or '\f':
                LineFeed();
                _pendingWrap = false;
                break;
            case '\r':
                _x = 0;
                _pendingWrap = false;
                break;
            case '\u000e':
                _shiftOut = true;
                break;
            case '\u000f':
                _shiftOut = false;
                break;
        }
    }

    void IVtHandler.EscDispatch(string intermediates, char final)
    {
        if (intermediates.Length == 0)
        {
            switch (final)
            {
                case '7':
                    SaveCursor();
                    break;
                case '8':
                    RestoreCursor();
                    break;
                case 'D':
                    LineFeed();
                    break;
                case 'E':
                    _x = 0;
                    LineFeed();
                    break;
                case 'H':
                    if (_x < _tabStops.Length)
                    {
                        _tabStops[_x] = true;
                    }

                    break;
                case 'M':
                    ReverseIndex();
                    break;
                case 'c':
                    FullReset();
                    break;
                case '=':
                    ApplicationKeypad = true;
                    break;
                case '>':
                    ApplicationKeypad = false;
                    break;
            }

            _pendingWrap = false;
            return;
        }

        switch (intermediates)
        {
            case "(":
                _lineDrawing = final == '0';
                break;
            case ")":
                _g1LineDrawing = final == '0';
                break;
            case "#" when final == '8':
                // DECALN: the screen full of E.
                for (var row = 0; row < Rows; row++)
                {
                    var line = _screen.Row(row);
                    for (var i = 0; i < Columns; i++)
                    {
                        line.Cells[i] = new Cell("E", CellStyle.Plain);
                    }
                }

                break;
        }
    }

    void IVtHandler.OscDispatch(string payload)
    {
        // A command was accepted (OSC 133;C): where its output starts, for the console that grows over the panels. zsh
        // and bash cannot tell their cursor row, the emulator can (issue #312).
        if (payload == "133;C")
        {
            _commandMark = (_y, Rows, Columns);
        }

        var separator = payload.IndexOf(';', StringComparison.Ordinal);
        var code = separator < 0 ? payload : payload[..separator];
        if (code is "0" or "2")
        {
            Title = separator < 0 ? string.Empty : payload[(separator + 1)..];
        }
    }

    void IVtHandler.CsiDispatch(char marker, CsiParams p, string intermediates, char final)
    {
        if (marker == '?')
        {
            switch (final)
            {
                case 'h':
                    SetPrivateModes(p, true);
                    break;
                case 'l':
                    SetPrivateModes(p, false);
                    break;
                case 'J':
                    EraseInDisplay(p.Get(0, 0));
                    break;
                case 'K':
                    EraseInLine(p.Get(0, 0));
                    break;
            }

            return;
        }

        if (marker is '>' or '=' or '<')
        {
            if (marker == '>' && final == 'c')
            {
                _replies.Append("\u001b[>0;10;1c");
            }

            return;
        }

        if (intermediates.Length > 0)
        {
            switch (intermediates, final)
            {
                case (" ", 'q'):
                    SetCursorStyle(p.Raw(0));
                    break;
                case ("!", 'p'):
                    SoftReset();
                    break;
            }

            return;
        }

        if (final != 'b')
        {
            _lastPrinted = final == 'm' ? _lastPrinted : null;
        }

        switch (final)
        {
            case '@':
                InsertCells(p.Get(0, 1));
                break;
            case 'A':
                MoveTo(_x, Math.Max(_y - p.Get(0, 1), _y >= _top ? _top : 0));
                break;
            case 'B':
            case 'e':
                MoveTo(_x, Math.Min(_y + p.Get(0, 1), _y <= _bottom ? _bottom : Rows - 1));
                break;
            case 'C':
            case 'a':
                MoveTo(_x + p.Get(0, 1), _y);
                break;
            case 'D':
                MoveTo(_x - p.Get(0, 1), _y);
                break;
            case 'E':
                MoveTo(0, Math.Min(_y + p.Get(0, 1), _y <= _bottom ? _bottom : Rows - 1));
                break;
            case 'F':
                MoveTo(0, Math.Max(_y - p.Get(0, 1), _y >= _top ? _top : 0));
                break;
            case 'G':
            case '`':
                MoveTo(p.Get(0, 1) - 1, _y);
                break;
            case 'H':
            case 'f':
                CursorPosition(p.Get(0, 1), p.Get(1, 1));
                break;
            case 'I':
                for (var i = p.Get(0, 1); i > 0; i--)
                {
                    _x = NextTab(_x);
                }

                _pendingWrap = false;
                break;
            case 'J':
                EraseInDisplay(p.Get(0, 0));
                break;
            case 'K':
                EraseInLine(p.Get(0, 0));
                break;
            case 'L':
                InsertLines(p.Get(0, 1));
                break;
            case 'M':
                DeleteLines(p.Get(0, 1));
                break;
            case 'P':
                DeleteCells(p.Get(0, 1));
                break;
            case 'S':
                for (var i = Math.Min(p.Get(0, 1), Rows); i > 0; i--)
                {
                    _screen.ScrollUp(_top, _bottom, _style.Erased, toScrollback: false);
                }

                break;
            case 'T':
                for (var i = Math.Min(p.Get(0, 1), Rows); i > 0; i--)
                {
                    _screen.ScrollDown(_top, _bottom, _style.Erased);
                }

                break;
            case 'X':
                {
                    var line = _screen.Row(_y);
                    var to = Math.Min(Columns, _x + p.Get(0, 1));
                    BreakWide(line, _x);
                    BreakWide(line, to);
                    line.Fill(_x, to, _style.Erased);
                    _pendingWrap = false;
                }

                break;
            case 'Z':
                for (var i = p.Get(0, 1); i > 0; i--)
                {
                    _x = PreviousTab(_x);
                }

                _pendingWrap = false;
                break;
            case 'b':
                if (_lastPrinted is { } last)
                {
                    for (var i = Math.Min(p.Get(0, 1), Columns * Rows); i > 0; i--)
                    {
                        ((IVtHandler)this).Print(last);
                    }
                }

                break;
            case 'c':
                if (p.Raw(0) <= 0)
                {
                    // A VT100 with advanced video.
                    _replies.Append("\u001b[?1;2c");
                }

                break;
            case 'd':
                MoveTo(_x, (OriginMode ? _top : 0) + p.Get(0, 1) - 1, clampToRegion: OriginMode);
                break;
            case 'g':
                if (p.Raw(0) <= 0 && _x < _tabStops.Length)
                {
                    _tabStops[_x] = false;
                }
                else if (p.Raw(0) == 3)
                {
                    Array.Clear(_tabStops);
                }

                break;
            case 'h':
            case 'l':
                for (var i = 0; i < p.Count; i++)
                {
                    if (p.Raw(i) == 4)
                    {
                        InsertMode = final == 'h';
                    }
                }

                break;
            case 'm':
                SelectGraphicRendition(p);
                break;
            case 'n':
                if (p.Raw(0) == 5)
                {
                    _replies.Append("\u001b[0n");
                }
                else if (p.Raw(0) == 6)
                {
                    var row = OriginMode ? _y - _top : _y;
                    _replies.Append(CultureInfo.InvariantCulture, $"\u001b[{row + 1};{CursorX + 1}R");
                }

                break;
            case 'r':
                {
                    var top = p.Get(0, 1) - 1;
                    var bottom = p.Get(1, Rows) - 1;
                    bottom = Math.Min(bottom, Rows - 1);
                    if (top < bottom)
                    {
                        _top = top;
                        _bottom = bottom;
                        CursorPosition(1, 1);
                    }
                }

                break;
            case 's':
                SaveCursor();
                break;
            case 'u':
                RestoreCursor();
                break;
        }
    }

    // ---- Operations ----

    private void AppendToPrevious(Rune rune)
    {
        var line = _screen.Row(_y);
        var x = _pendingWrap ? _x : _x - 1;
        if (x < 0)
        {
            return;
        }

        if (line.Cells[x].IsWideTail && x > 0)
        {
            x--;
        }

        if (line.Cells[x].Text is { } text)
        {
            line.Cells[x] = line.Cells[x] with { Text = text + rune.ToString() };
        }
    }

    /// <summary>Writing over half of a wide character blanks its other half.</summary>
    private void BreakWide(TermLine line, int x)
    {
        if (x <= 0 || x >= Columns)
        {
            if (x == 0 && Columns > 1 && line.Cells[0].Text is { Length: > 0 } && line.Cells[1].IsWideTail)
            {
                line.Cells[1] = Cell.Blank(line.Cells[1].Style);
            }

            return;
        }

        if (line.Cells[x].IsWideTail)
        {
            line.Cells[x - 1] = Cell.Blank(line.Cells[x - 1].Style);
            line.Cells[x] = Cell.Blank(line.Cells[x].Style);
        }
        else if (x + 1 < Columns && line.Cells[x + 1].IsWideTail)
        {
            line.Cells[x + 1] = Cell.Blank(line.Cells[x + 1].Style);
        }
    }

    private void LineFeed()
    {
        if (_y == _bottom)
        {
            _screen.ScrollUp(_top, _bottom, _style.Erased);
        }
        else if (_y < Rows - 1)
        {
            _y++;
        }
    }

    private void ReverseIndex()
    {
        if (_y == _top)
        {
            _screen.ScrollDown(_top, _bottom, _style.Erased);
        }
        else if (_y > 0)
        {
            _y--;
        }
    }

    private void MoveTo(int x, int y, bool clampToRegion = false)
    {
        _x = Math.Clamp(x, 0, Columns - 1);
        _y = clampToRegion ? Math.Clamp(y, _top, _bottom) : Math.Clamp(y, 0, Rows - 1);
        _pendingWrap = false;
    }

    private void CursorPosition(int row, int column)
    {
        if (OriginMode)
        {
            MoveTo(column - 1, _top + row - 1, clampToRegion: true);
        }
        else
        {
            MoveTo(column - 1, row - 1);
        }
    }

    private void EraseInLine(int mode)
    {
        var line = _screen.Row(_y);
        var (from, to) = mode switch
        {
            0 => (_x, Columns),
            1 => (0, _x + 1),
            _ => (0, Columns),
        };
        BreakWide(line, from);
        BreakWide(line, to);
        line.Fill(from, to, _style.Erased);
        if (to == Columns)
        {
            line.Wrapped = false;
        }

        _pendingWrap = false;
    }

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (var row = _y + 1; row < Rows; row++)
                {
                    ClearRow(row);
                }

                break;
            case 1:
                EraseInLine(1);
                for (var row = 0; row < _y; row++)
                {
                    ClearRow(row);
                }

                break;
            case 2:
                for (var row = 0; row < Rows; row++)
                {
                    ClearRow(row);
                }

                break;
            case 3:
                _screen.ClearScrollback();
                break;
        }

        _pendingWrap = false;
    }

    private void ClearRow(int row)
    {
        var line = _screen.Row(row);
        line.Clear(_style.Erased);
        line.Wrapped = false;
    }

    private void InsertCells(int count)
    {
        var line = _screen.Row(_y);
        count = Math.Min(count, Columns - _x);
        BreakWide(line, _x);
        Array.Copy(line.Cells, _x, line.Cells, _x + count, Columns - _x - count);
        line.Fill(_x, _x + count, _style.Erased);
        if (line.Cells[Columns - 1].Text is { Length: > 0 } && Columns > 1 && !line.Cells[Columns - 1].IsWideTail
            && CharWidth.Of(Rune.GetRuneAt(line.Cells[Columns - 1].Text!, 0)) == 2)
        {
            line.Cells[Columns - 1] = Cell.Blank(_style.Erased);
        }

        _pendingWrap = false;
    }

    private void DeleteCells(int count)
    {
        var line = _screen.Row(_y);
        count = Math.Min(count, Columns - _x);
        BreakWide(line, _x);
        BreakWide(line, _x + count);
        Array.Copy(line.Cells, _x + count, line.Cells, _x, Columns - _x - count);
        line.Fill(Columns - count, Columns, _style.Erased);
        _pendingWrap = false;
    }

    private void InsertLines(int count)
    {
        if (_y < _top || _y > _bottom)
        {
            return;
        }

        for (var i = Math.Min(count, _bottom - _y + 1); i > 0; i--)
        {
            _screen.ScrollDown(_y, _bottom, _style.Erased);
        }

        _x = 0;
        _pendingWrap = false;
    }

    private void DeleteLines(int count)
    {
        if (_y < _top || _y > _bottom)
        {
            return;
        }

        for (var i = Math.Min(count, _bottom - _y + 1); i > 0; i--)
        {
            // Deleted lines never go to the scrollback.
            _screen.ScrollUp(_y, _bottom, _style.Erased, toScrollback: false);
        }

        _x = 0;
        _pendingWrap = false;
    }

    private void SetPrivateModes(CsiParams p, bool on)
    {
        for (var i = 0; i < p.Count; i++)
        {
            switch (p.Raw(i))
            {
                case 1:
                    ApplicationCursorKeys = on;
                    break;
                case 5:
                    ReverseVideo = on;
                    break;
                case 6:
                    OriginMode = on;
                    CursorPosition(1, 1);
                    break;
                case 7:
                    AutoWrap = on;
                    break;
                case 12:
                    CursorBlinks = on;
                    break;
                case 25:
                    CursorVisible = on;
                    break;
                case 47:
                case 1047:
                    SwitchScreen(on, clear: p.Raw(i) == 1047);
                    break;
                case 1049:
                    if (on)
                    {
                        SaveCursor();
                        SwitchScreen(true, clear: true);
                    }
                    else
                    {
                        SwitchScreen(false, clear: false);
                        RestoreCursor();
                    }

                    break;
                case 1004:
                    FocusEvents = on;
                    break;
                case 2004:
                    BracketedPaste = on;
                    break;
                case 9001:
                    Win32InputMode = on;
                    break;
            }
        }
    }

    private void SwitchScreen(bool alternate, bool clear)
    {
        var target = alternate ? _alternate : _main;
        if (target == _screen)
        {
            return;
        }

        _screen = target;
        if (alternate && clear)
        {
            for (var row = 0; row < Rows; row++)
            {
                ClearRow(row);
            }
        }

        _pendingWrap = false;
    }

    private void SetCursorStyle(int value)
    {
        (CursorShape, CursorBlinks) = value switch
        {
            1 or 0 or -1 => (CursorShape.Block, true),
            2 => (CursorShape.Block, false),
            3 => (CursorShape.Underline, true),
            4 => (CursorShape.Underline, false),
            5 => (CursorShape.Bar, true),
            6 => (CursorShape.Bar, false),
            _ => (CursorShape, CursorBlinks),
        };
    }

    private void SaveCursor() => _saved = (_x, _y, _style, OriginMode, _lineDrawing);

    private void RestoreCursor()
    {
        (_x, _y, _style, OriginMode, _lineDrawing) = _saved;
        _x = Math.Min(_x, Columns - 1);
        _y = Math.Min(_y, Rows - 1);
        _pendingWrap = false;
    }

    private void SoftReset()
    {
        CursorVisible = true;
        InsertMode = false;
        OriginMode = false;
        AutoWrap = true;
        ApplicationCursorKeys = false;
        ApplicationKeypad = false;
        _top = 0;
        _bottom = Rows - 1;
        _style = CellStyle.Plain;
        _lineDrawing = false;
        _g1LineDrawing = false;
        _shiftOut = false;
        _saved = (0, 0, CellStyle.Plain, false, false);
        _pendingWrap = false;
    }

    private void FullReset()
    {
        SwitchScreen(false, clear: false);
        SoftReset();
        BracketedPaste = false;
        FocusEvents = false;
        ReverseVideo = false;
        CursorShape = CursorShape.Block;
        CursorBlinks = true;
        for (var row = 0; row < Rows; row++)
        {
            ClearRow(row);
        }

        _screen.ClearScrollback();
        _x = 0;
        _y = 0;
        ResetTabStops(Columns);
    }

    private void ResetTabStops(int columns)
    {
        _tabStops = new bool[columns];
        for (var i = 8; i < columns; i += 8)
        {
            _tabStops[i] = true;
        }
    }

    private int NextTab(int x)
    {
        for (var i = x + 1; i < Columns; i++)
        {
            if (i < _tabStops.Length && _tabStops[i])
            {
                return i;
            }
        }

        return Columns - 1;
    }

    private int PreviousTab(int x)
    {
        for (var i = x - 1; i > 0; i--)
        {
            if (i < _tabStops.Length && _tabStops[i])
            {
                return i;
            }
        }

        return 0;
    }

    private void SelectGraphicRendition(CsiParams p)
    {
        if (p.Count == 0)
        {
            _style = CellStyle.Plain;
            return;
        }

        var fg = _style.Foreground;
        var bg = _style.Background;
        var flags = _style.Flags;
        for (var i = 0; i < p.Count; i++)
        {
            var code = p.Raw(i);
            switch (code)
            {
                case -1 or 0:
                    fg = TermColor.Default;
                    bg = TermColor.Default;
                    flags = TextAttributes.None;
                    break;
                case 1: flags |= TextAttributes.Bold; break;
                case 2: flags |= TextAttributes.Faint; break;
                case 3: flags |= TextAttributes.Italic; break;
                case 4:
                    var sub = p.Sub(i);
                    if (sub.Length > 1 && sub[1] == 0)
                    {
                        flags &= ~TextAttributes.Underline;
                    }
                    else
                    {
                        flags |= TextAttributes.Underline;
                    }

                    break;
                case 7: flags |= TextAttributes.Inverse; break;
                case 8: flags |= TextAttributes.Hidden; break;
                case 9: flags |= TextAttributes.Strike; break;
                case 21: flags |= TextAttributes.Underline; break;
                case 22: flags &= ~(TextAttributes.Bold | TextAttributes.Faint); break;
                case 23: flags &= ~TextAttributes.Italic; break;
                case 24: flags &= ~TextAttributes.Underline; break;
                case 27: flags &= ~TextAttributes.Inverse; break;
                case 28: flags &= ~TextAttributes.Hidden; break;
                case 29: flags &= ~TextAttributes.Strike; break;
                case >= 30 and <= 37: fg = TermColor.Palette(code - 30); break;
                case 38: fg = ExtendedColor(p, ref i, fg); break;
                case 39: fg = TermColor.Default; break;
                case >= 40 and <= 47: bg = TermColor.Palette(code - 40); break;
                case 48: bg = ExtendedColor(p, ref i, bg); break;
                case 49: bg = TermColor.Default; break;
                case >= 90 and <= 97: fg = TermColor.Palette(code - 90 + 8); break;
                case >= 100 and <= 107: bg = TermColor.Palette(code - 100 + 8); break;
            }
        }

        _style = new CellStyle(fg, bg, flags);
    }

    /// <summary>38/48 ; 5 ; n or 38/48 ; 2 ; r ; g ; b, with ':' sub-parameters too.</summary>
    private static TermColor ExtendedColor(CsiParams p, ref int i, TermColor current)
    {
        var sub = p.Sub(i);
        if (sub.Length > 1)
        {
            // Colon form: 38:5:n, 38:2:r:g:b or 38:2:colorspace:r:g:b.
            if (sub[1] == 5 && sub.Length > 2)
            {
                return TermColor.Palette(sub[2]);
            }

            if (sub[1] == 2)
            {
                var offset = sub.Length >= 6 ? 3 : 2;
                if (sub.Length >= offset + 3)
                {
                    return TermColor.Rgb((byte)sub[offset], (byte)sub[offset + 1], (byte)sub[offset + 2]);
                }
            }

            return current;
        }

        var kind = p.Raw(i + 1);
        if (kind == 5 && i + 2 < p.Count)
        {
            var index = p.Raw(i + 2);
            i += 2;
            return index >= 0 ? TermColor.Palette(index) : current;
        }

        if (kind == 2 && i + 4 < p.Count)
        {
            var color = TermColor.Rgb((byte)Math.Max(0, p.Raw(i + 2)), (byte)Math.Max(0, p.Raw(i + 3)), (byte)Math.Max(0, p.Raw(i + 4)));
            i += 4;
            return color;
        }

        return current;
    }

    // DEC Special Graphics for 0x5F..0x7E.
    private static readonly char[] LineDrawing =
    [
        ' ', '◆', '▒', '␉', '␌', '␍', '␊', '°', '±', '␤', '␋', '┘', '┐', '┌', '└', '┼',
        '⎺', '⎻', '─', '⎼', '⎽', '├', '┤', '┴', '┬', '│', '≤', '≥', 'π', '≠', '£', '·',
    ];
}
