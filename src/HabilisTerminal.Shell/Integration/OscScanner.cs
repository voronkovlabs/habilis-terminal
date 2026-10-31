using System.Text;

namespace HabilisTerminal.Shell.Integration;

/// <summary>
/// Finds OSC sequences (<c>ESC ] payload BEL</c> or <c>ESC ] payload ESC \</c>) in a terminal output stream.
/// The stream itself is not modified; sequences split across chunks are handled.
/// </summary>
public sealed class OscScanner
{
    private const char Esc = '\u001b';
    private const char Bel = '\a';
    private const int MaxPayloadLength = 8192;

    private readonly StringBuilder _payload = new();
    private State _state = State.Text;

    private enum State
    {
        Text,
        Escape,
        Payload,
        PayloadEscape,
    }

    /// <summary>Scans a chunk and invokes <paramref name="onSequence"/> for every completed payload.</summary>
    public void Scan(ReadOnlySpan<char> chunk, Action<string> onSequence)
    {
        foreach (var c in chunk)
        {
            switch (_state)
            {
                case State.Text:
                    if (c == Esc)
                    {
                        _state = State.Escape;
                    }

                    break;

                case State.Escape:
                    _state = c switch
                    {
                        ']' => State.Payload,
                        Esc => State.Escape,
                        _ => State.Text,
                    };
                    _payload.Clear();
                    break;

                case State.Payload:
                    if (c == Bel)
                    {
                        Complete(onSequence);
                    }
                    else if (c == Esc)
                    {
                        _state = State.PayloadEscape;
                    }
                    else if (_payload.Length < MaxPayloadLength)
                    {
                        _payload.Append(c);
                    }
                    else
                    {
                        // Runaway sequence: drop it.
                        _payload.Clear();
                        _state = State.Text;
                    }

                    break;

                case State.PayloadEscape:
                    if (c == '\\')
                    {
                        Complete(onSequence);
                    }
                    else
                    {
                        // Any other escape aborts the OSC and starts a new sequence.
                        _payload.Clear();
                        _state = c == ']' ? State.Payload : State.Text;
                    }

                    break;
            }
        }
    }

    private void Complete(Action<string> onSequence)
    {
        var payload = _payload.ToString();
        _payload.Clear();
        _state = State.Text;
        onSequence(payload);
    }
}
