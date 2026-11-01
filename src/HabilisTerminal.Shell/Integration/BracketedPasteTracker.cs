namespace HabilisTerminal.Shell.Integration;

/// <summary>
/// Follows DECSET / DECRST 2004 (bracketed paste) in terminal output; a sequence may be split between two chunks.
/// </summary>
public sealed class BracketedPasteTracker
{
    private const string On = "\u001b[?2004h";
    private const string Off = "\u001b[?2004l";
    private string _tail = string.Empty;

    public bool Enabled { get; private set; }

    public void Feed(string text)
    {
        var window = _tail + text;
        var on = window.LastIndexOf(On, StringComparison.Ordinal);
        var off = window.LastIndexOf(Off, StringComparison.Ordinal);
        if (on >= 0 || off >= 0)
        {
            Enabled = on > off;
        }

        // Shorter than a whole sequence, so a sequence is never counted twice.
        _tail = window.Length >= On.Length ? window[^(On.Length - 1)..] : window;
    }

    /// <summary>Text as the terminal should send it: line breaks as CR (like a typed Enter), bracketed when enabled.</summary>
    public string Wrap(string text)
    {
        var normalized = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        if (!Enabled)
        {
            return normalized;
        }

        // A pasted end marker would let the rest of the text run as typed input.
        return "\u001b[200~" + normalized.Replace("\u001b[201~", string.Empty, StringComparison.Ordinal) + "\u001b[201~";
    }
}
