namespace HabilisTerminal.Core.Panels;

/// <summary>
/// How far a running command's output has overflowed the console (issue #82, experiment): the rows written since the
/// command started, beyond the rows that were free under the prompt. Counts line feeds and the wraps of long lines,
/// skips escape sequences, and notices the alternate screen (full-screen programs such as vim, less, htop).
/// Not thread-safe: fed from the shell's output thread under the caller's lock.
/// </summary>
public sealed class TerminalOverflow
{
    private int _freeRows;
    private int _rows;
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

    /// <summary>Rows written beyond the free space under the prompt since the command started; 0 between commands.</summary>
    public int OverflowRows => _active ? Math.Max(0, _rows - _freeRows) : 0;

    /// <summary>A full-screen program switched to the alternate screen and has not switched back.</summary>
    public bool AlternateScreen { get; private set; }

    /// <summary>A command was accepted: output starts at <paramref name="cursorRow"/> (0-based) of a console that size.</summary>
    public void CommandStarted(int cursorRow, int consoleRows, int consoleColumns)
    {
        _active = true;
        _freeRows = Math.Max(0, consoleRows - cursorRow - 1);
        _rows = 0;
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
            _rows++;
        }

        _column = 0;
    }

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
        else if (final == 'G' || final == 'H' || final == 'f')
        {
            // Cursor to a column (or home): wrapping restarts from there.
            var parts = parameters.Split(';');
            var columnText = final == 'G' ? parts[0] : parts.Length > 1 ? parts[1] : "1";
            _column = int.TryParse(columnText, out var column) && column > 0 ? column - 1 : 0;
        }
    }
}
