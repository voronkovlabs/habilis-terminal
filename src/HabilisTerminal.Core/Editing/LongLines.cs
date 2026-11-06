namespace HabilisTerminal.Core.Editing;

/// <summary>
/// Syntax colours are worked out with regular expressions over a whole line, and the cost grows much faster than the
/// line (issue #163): measured, 5 000 characters cost nothing, 20 000 about 0.8 s for each key, 100 000 about 20 s and
/// 300 000 over 40 s, all on the UI thread. Without colours a line of a million characters is still usable.
/// </summary>
public static class LongLines
{
    /// <summary>A line longer than this turns syntax colours off.</summary>
    public const int ColorLimit = 10_000;

    /// <summary>
    /// A line longer than this, in bytes, is not for the text editor control at all, even without colours: 1 000 000
    /// characters work, 10 000 000 froze the window for over a minute. The paged view has no such limit.
    /// </summary>
    public const int EditorLimit = 2_000_000;

    /// <summary>The file has a line too long for the text editor control. Reads the file; a line end is the same byte in every encoding used here.</summary>
    public static bool HasHugeLine(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        var run = 0L;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var rest = buffer.AsSpan(0, read);
            while (rest.Length > 0)
            {
                var end = rest.IndexOfAny((byte)'\r', (byte)'\n');
                if (end < 0)
                {
                    run += rest.Length;
                    break;
                }

                if (run + end > EditorLimit)
                {
                    return true;
                }

                run = 0;
                rest = rest[(end + 1)..];
            }

            if (run > EditorLimit)
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasHugeLine(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return HasHugeLine(stream);
    }

    public static bool TooLongToColor(string text)
    {
        var span = text.AsSpan();
        while (span.Length > ColorLimit)
        {
            var end = span.IndexOfAny('\r', '\n');
            if (end < 0 || end > ColorLimit)
            {
                return true;
            }

            span = span[(end + 1)..];
        }

        return false;
    }
}
