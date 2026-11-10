using System.Text;
using System.Text.RegularExpressions;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.Core.Search;

/// <summary>
/// Text inside a file, for Alt+F7 (issue #198). A text file is read in the encoding the viewer would pick; a file that
/// does not look like text is searched as ANSI, UTF-8 and UTF-16, as names and messages in programs often are. A regular
/// expression is matched within a line (issue #209).
/// </summary>
public sealed class TextFinder
{
    private const int SampleBytes = 64 * 1024;
    private const int ChunkChars = 1 << 20;

    private readonly string _text;
    private readonly StringComparison _comparison;
    private readonly bool _wholeWords;
    private readonly Regex? _regex;

    /// <exception cref="ArgumentException">Empty text, or an invalid regular expression.</exception>
    public TextFinder(string text, bool matchCase, bool wholeWords, bool regex = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        _text = text;
        _comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        _wholeWords = wholeWords;
        if (regex)
        {
            _regex = Compile(text, matchCase, wholeWords);
        }
    }

    /// <summary>The expression as Alt+F7 uses it; throws <see cref="ArgumentException"/> when it is invalid.</summary>
    public static Regex Compile(string pattern, bool matchCase, bool wholeWords)
    {
        var options = RegexOptions.Multiline | RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        return new Regex(wholeWords ? $@"\b(?:{pattern})\b" : pattern, options, TimeSpan.FromSeconds(5));
    }

    /// <summary>Whether the file holds the text. A file that cannot be read does not.</summary>
    public bool IsIn(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            return IsInContent(stream, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a stream holds the text, its encoding guessed as for a file. A stream that cannot seek (an entry inside an
    /// archive) is copied first: to memory when small, else to a temporary file deleted on close.
    /// </summary>
    public bool IsInContent(Stream stream, CancellationToken cancellationToken = default)
    {
        if (!stream.CanSeek)
        {
            using var copy = CopyForSeeking(stream, cancellationToken);
            return IsInContent(copy, cancellationToken);
        }

        try
        {
            var sample = new byte[SampleBytes];
            var read = stream.ReadAtLeast(sample, sample.Length, throwOnEndOfStream: false);
            var span = sample.AsSpan(0, read);
            var encodings = TextEncoding.LooksLikeText(span)
                ? [TextEncoding.Detect(span).Encoding]
                : new[] { TextEncoding.Ansi.Encoding, Encoding.UTF8, Encoding.Unicode };
            foreach (var encoding in encodings)
            {
                stream.Position = 0;
                if (IsIn(stream, encoding, cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Stream CopyForSeeking(Stream stream, CancellationToken cancellationToken)
    {
        const int InMemory = 16 * 1024 * 1024;
        var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            memory.Write(buffer, 0, read);
            if (memory.Length > InMemory)
            {
                var file = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
                memory.Position = 0;
                memory.CopyTo(file);
                memory.Dispose();
                stream.CopyTo(file);
                file.Position = 0;
                return file;
            }
        }

        memory.Position = 0;
        return memory;
    }

    /// <summary>Decodes in chunks, keeping the end of each chunk so that a match across two of them is found.</summary>
    public bool IsIn(Stream stream, Encoding encoding, CancellationToken cancellationToken = default)
    {
        if (_regex is not null)
        {
            return IsInByLines(stream, encoding, _regex, cancellationToken);
        }

        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        var buffer = new char[ChunkChars];
        var carry = string.Empty;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = carry + new string(buffer, 0, read);
            var atEnd = reader.EndOfStream;
            if (Find(chunk, atEnd))
            {
                return true;
            }

            // The text minus one character, plus one before it for the word check.
            var keep = Math.Min(chunk.Length, _text.Length);
            carry = chunk[^keep..];
        }

        return false;
    }

    /// <summary>
    /// Chunks cut after their last line end, the rest carried to the next chunk, so a line is never split. A line longer
    /// than a chunk is split after all: its parts are matched on their own.
    /// </summary>
    private static bool IsInByLines(Stream stream, Encoding encoding, Regex regex, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        var buffer = new char[ChunkChars];
        var carry = string.Empty;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = carry + new string(buffer, 0, read);
            var cut = reader.EndOfStream ? chunk.Length : chunk.LastIndexOf('\n') + 1;
            if (cut <= 0)
            {
                cut = chunk.Length;
            }

            try
            {
                if (regex.IsMatch(chunk.AsSpan(0, cut)))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological expression on this text: not a match, and the search goes on.
            }

            carry = chunk[cut..];
        }

        return carry.Length > 0 && regex.IsMatch(carry);
    }

    /// <summary>A match at the very end of a chunk that is not the last is left for the next one, which can tell whether a word goes on.</summary>
    private bool Find(string chunk, bool isLast)
    {
        var from = 0;
        while (from <= chunk.Length - _text.Length)
        {
            var index = chunk.IndexOf(_text, from, _comparison);
            if (index < 0)
            {
                return false;
            }

            var end = index + _text.Length;
            if (!_wholeWords)
            {
                return true;
            }

            var startsWord = index == 0 || !IsWordChar(chunk[index - 1]);
            if (end == chunk.Length && !isLast)
            {
                return false; // decided by the next chunk, which carries this one's end
            }

            if (startsWord && (end == chunk.Length || !IsWordChar(chunk[end])))
            {
                return true;
            }

            from = index + 1;
        }

        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
