namespace HabilisTerminal.Core.Panels;

/// <summary>
/// How far a running command's output has overflowed the console (issue #82): how many rows the cursor went below the
/// console's bottom. It follows the cursor (line feeds, wraps of long lines, cursor moves), because progress bars redraw
/// in place with a line feed and a move back up (issue #102). Skips other escape sequences, and notices the alternate
/// screen (full-screen programs such as vim, less, htop).
/// Not thread-safe: fed from the shell's output thread under the caller's lock.
/// </summary>
public sealed class TerminalOverflow
{
    private int _consoleRows;
    private int _row;
    private int _overflow;
    private int _column;
    private int _columns = 80;
    private bool _active;
    private State _state;
    private readonly System.Text.StringBuilder _sequence = new();

    private enum State
    {
        Text,
        Escape,
        Csi,
        Osc,
        OscEscape,
    }

    /// <summary>The most rows the cursor went below the console's bottom since the command started; 0 between commands.</summary>
    public int OverflowRows => _active ? _overflow : 0;

    /// <summary>A full-screen program switched to the alternate screen and has not switched back.</summary>
    public bool AlternateScreen { get; private set; }

    /// <summary>A command was accepted: output starts at <paramref name="cursorRow"/> (0-based) of a console that size.</summary>
    public void CommandStarted(int cursorRow, int consoleRows, int consoleColumns)
    {
        _active = true;
        _consoleRows = Math.Max(1, consoleRows);
        _row = Math.Clamp(cursorRow, 0, _consoleRows - 1);
        _overflow = 0;
        _column = 0;
        _columns = Math.Max(1, consoleColumns);
    }

    /// <summary>The prompt is back: no more growth until the next command.</summary>
    public void CommandFinished()
    {
        _active = false;
        AlternateScreen = false;
    }

    public void Feed(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            switch (_state)
            {
                case State.Text:
                    Text(c);
                    break;
                case State.Escape:
                    _state = c switch { '[' => State.Csi, ']' => State.Osc, _ => State.Text };
                    _sequence.Clear();
                    break;
                case State.Csi:
                    if (c is >= '@' and <= '~')
                    {
                        Csi(_sequence.ToString(), c);
                        _state = State.Text;
                    }
                    else
                    {
                        _sequence.Append(c);
                    }

                    break;
                case State.Osc:
                    _state = c switch { '\a' => State.Text, '\x1b' => State.OscEscape, _ => State.Osc };
                    break;
                case State.OscEscape:
                    _state = c == '\\' ? State.Text : State.Osc;
                    break;
            }
        }
    }

    private void Text(char c)
    {
        switch (c)
        {
            case '\x1b':
                _state = State.Escape;
                break;
            case '\n':
                NewRow();
                break;
            case '\r':
                _column = 0;
                break;
            case '\b':
                _column = Math.Max(0, _column - 1);
                break;
            case '\t':
                _column = Math.Min(_columns, ((_column / 8) + 1) * 8);
                break;
            default:
                if (c < ' ' || char.IsLowSurrogate(c))
                {
                    break;
                }

                // A character in the last column wraps only when the next one comes (as terminals do).
                if (_column >= _columns)
                {
                    NewRow();
                }

                _column++;
                break;
        }
    }

    private void NewRow()
    {
        if (_active && !AlternateScreen)
        {
            _row++;
            _overflow = Math.Max(_overflow, _row - (_consoleRows - 1));
        }

        _column = 0;
    }

    /// <summary>Moves stop at the screen's edges; the screen has grown by the overflow so far (the window grows it).</summary>
    private void MoveToRow(int row)
    {
        if (_active && !AlternateScreen)
        {
            _row = Math.Clamp(row, 0, _consoleRows - 1 + _overflow);
        }
    }

    private static int Number(string text, int fallback = 1) =>
        int.TryParse(text, out var value) && value > 0 ? value : fallback;

    private void Csi(string parameters, char final)
    {
        // DECSET / DECRST 1049, 1047, 47: the alternate screen buffer.
        if (final is 'h' or 'l' && parameters.StartsWith('?'))
        {
            foreach (var mode in parameters[1..].Split(';'))
            {
                if (mode is "1049" or "1047" or "47")
                {
                    AlternateScreen = final == 'h';
                }
            }
        }
        else
        {
            var parts = parameters.Split(';');
            switch (final)
            {
                case 'G':
                    _column = Number(parts[0]) - 1;
                    break;
                case 'H' or 'f':
                    MoveToRow(Number(parts[0]) - 1);
                    _column = (parts.Length > 1 ? Number(parts[1]) : 1) - 1;
                    break;
                case 'A':
                    MoveToRow(_row - Number(parts[0]));
                    break;
                case 'B':
                    MoveToRow(_row + Number(parts[0]));
                    break;
                case 'F':
                    MoveToRow(_row - Number(parts[0]));
                    _column = 0;
                    break;
                case 'E':
                    MoveToRow(_row + Number(parts[0]));
                    _column = 0;
                    break;
                case 'd':
                    MoveToRow(Number(parts[0]) - 1);
                    break;
            }
        }
    }
}
