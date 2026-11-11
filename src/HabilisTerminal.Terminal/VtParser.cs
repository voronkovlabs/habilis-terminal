using System.Text;

namespace HabilisTerminal.Terminal;

/// <summary>What the parser found, in order.</summary>
internal interface IVtHandler
{
    void Print(Rune rune);

    void Execute(char control);

    /// <param name="marker">The private marker (<c>?</c>, <c>&gt;</c>, <c>=</c>, <c>&lt;</c>) or '\0'.</param>
    void CsiDispatch(char marker, CsiParams parameters, string intermediates, char final);

    void EscDispatch(string intermediates, char final);

    void OscDispatch(string payload);
}

/// <summary>CSI parameters: numbers separated by ';', each with optional ':' sub-parameters.</summary>
internal sealed class CsiParams
{
    private readonly List<int[]> _items = [];

    public int Count => _items.Count;

    /// <summary>The parameter, or <paramref name="fallback"/> when missing or zero (CSI's "0 means default").</summary>
    public int Get(int index, int fallback)
    {
        if (index >= _items.Count || _items[index].Length == 0)
        {
            return fallback;
        }

        var value = _items[index][0];
        return value <= 0 ? fallback : value;
    }

    /// <summary>The raw value, -1 when missing.</summary>
    public int Raw(int index) => index < _items.Count && _items[index].Length > 0 ? _items[index][0] : -1;

    public int[] Sub(int index) => index < _items.Count ? _items[index] : [];

    internal void Parse(StringBuilder text)
    {
        _items.Clear();
        if (text.Length == 0)
        {
            return;
        }

        foreach (var part in text.ToString().Split(';'))
        {
            if (part.Length == 0)
            {
                _items.Add([]);
                continue;
            }

            var subs = part.Split(':');
            var values = new int[subs.Length];
            for (var i = 0; i < subs.Length; i++)
            {
                values[i] = int.TryParse(subs[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v)
                    ? Math.Min(v, 65535)
                    : -1;
            }

            _items.Add(values);
        }
    }
}

/// <summary>
/// The DEC ANSI parser as described by Paul Williams (vt100.net/emu/dec_ansi_parser), on UTF-16 text. DCS, SOS, PM
/// and APC strings are skipped.
/// </summary>
internal sealed class VtParser(IVtHandler handler)
{
    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        OscString,
        IgnoredString,
    }

    private const int MaxOsc = 64 * 1024;

    private readonly StringBuilder _params = new();
    private readonly StringBuilder _intermediates = new();
    private readonly StringBuilder _osc = new();
    private readonly CsiParams _parsed = new();
    private State _state;
    private char _marker;
    private char _highSurrogate;
    private bool _stringEscape;

    public void Feed(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            Step(c);
        }
    }

    private void Step(char c)
    {
        // Strings end with BEL or ST (ESC \); anything else in them is content.
        if (_state is State.OscString or State.IgnoredString)
        {
            if (_stringEscape)
            {
                _stringEscape = false;
                if (c == '\\')
                {
                    EndString();
                    return;
                }

                EndString();
                Enter(State.Escape);
                Step(c);
                return;
            }

            switch (c)
            {
                case '\u0007':
                    EndString();
                    return;
                case '\u001b':
                    _stringEscape = true;
                    return;
                case '\u0018' or '\u001a':
                    _osc.Clear();
                    _state = State.Ground;
                    return;
            }

            if (_state == State.OscString && _osc.Length < MaxOsc)
            {
                _osc.Append(c);
            }

            return;
        }

        switch (c)
        {
            case '\u0018' or '\u001a':
                _state = State.Ground;
                return;
            case '\u001b':
                Enter(State.Escape);
                return;
        }

        if (c < 0x20 || c == 0x7F)
        {
            if (c != 0x7F)
            {
                handler.Execute(c);
            }

            return;
        }

        switch (_state)
        {
            case State.Ground:
                PrintChar(c);
                break;

            case State.Escape:
                if (c is >= ' ' and <= '/')
                {
                    _intermediates.Append(c);
                    _state = State.EscapeIntermediate;
                }
                else if (c == '[')
                {
                    Enter(State.CsiEntry);
                }
                else if (c == ']')
                {
                    _osc.Clear();
                    _state = State.OscString;
                }
                else if (c is 'P' or 'X' or '^' or '_')
                {
                    _state = State.IgnoredString;
                }
                else
                {
                    handler.EscDispatch(string.Empty, c);
                    _state = State.Ground;
                }

                break;

            case State.EscapeIntermediate:
                if (c is >= ' ' and <= '/')
                {
                    _intermediates.Append(c);
                }
                else
                {
                    handler.EscDispatch(_intermediates.ToString(), c);
                    _state = State.Ground;
                }

                break;

            case State.CsiEntry:
            case State.CsiParam:
                if (c is >= '0' and <= '9' or ';' or ':')
                {
                    _params.Append(c);
                    _state = State.CsiParam;
                }
                else if (c is >= '<' and <= '?')
                {
                    if (_state == State.CsiEntry && _marker == '\0')
                    {
                        _marker = c;
                    }
                    else
                    {
                        _state = State.CsiIgnore;
                    }
                }
                else if (c is >= ' ' and <= '/')
                {
                    _intermediates.Append(c);
                    _state = State.CsiIntermediate;
                }
                else
                {
                    DispatchCsi(c);
                }

                break;

            case State.CsiIntermediate:
                if (c is >= ' ' and <= '/')
                {
                    _intermediates.Append(c);
                }
                else if (c is >= '0' and <= '?')
                {
                    _state = State.CsiIgnore;
                }
                else
                {
                    DispatchCsi(c);
                }

                break;

            case State.CsiIgnore:
                if (c is >= '@' and <= '~')
                {
                    _state = State.Ground;
                }

                break;
        }
    }

    private void PrintChar(char c)
    {
        if (char.IsHighSurrogate(c))
        {
            _highSurrogate = c;
            return;
        }

        if (char.IsLowSurrogate(c))
        {
            if (_highSurrogate != '\0')
            {
                handler.Print(new Rune(_highSurrogate, c));
                _highSurrogate = '\0';
            }

            return;
        }

        _highSurrogate = '\0';
        handler.Print(new Rune(c));
    }

    private void DispatchCsi(char final)
    {
        _parsed.Parse(_params);
        handler.CsiDispatch(_marker, _parsed, _intermediates.ToString(), final);
        _state = State.Ground;
    }

    private void EndString()
    {
        if (_state == State.OscString)
        {
            handler.OscDispatch(_osc.ToString());
        }

        _osc.Clear();
        _state = State.Ground;
    }

    private void Enter(State state)
    {
        _state = state;
        _params.Clear();
        _intermediates.Clear();
        _marker = '\0';
    }
}
