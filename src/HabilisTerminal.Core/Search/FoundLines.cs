using System.Text;
using System.Text.RegularExpressions;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.Core.Search;

/// <summary>A line of a found file that holds the text (issue #415), as the results tab shows it.</summary>
/// <param name="Number">The line number, from 1.</param>
/// <param name="Column">Where the first match starts in the line, from 1.</param>
/// <param name="Text">The line, cut to a readable length around the first match and without leading blanks.</param>
/// <param name="MatchStart">Where the first match starts in <see cref="Text"/>.</param>
/// <param name="MatchLength">How long it is in <see cref="Text"/>.</param>
public sealed record FoundLine(int Number, int Column, string Text, int MatchStart, int MatchLength);

/// <summary>
/// The lines that hold the text, read after the search has found the file (issue #415): only found files are read a
/// second time. A file that does not look like text has no lines to show.
/// </summary>
public static class FoundLines
{
    /// <summary>Lines kept per file: a file matching everywhere would bury the rest.</summary>
    public const int MaxPerFile = 500;

    private const int MaxShown = 240;
    private const int SampleBytes = 64 * 1024;

    /// <summary>The matching lines, or null when the file is not text or cannot be read.</summary>
    public static IReadOnlyList<FoundLine>? Read(string path, string text, bool matchCase, bool wholeWords, bool regex, CancellationToken cancellationToken = default)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            return Read(stream, text, matchCase, wholeWords, regex, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The matching lines of a seekable stream, its encoding guessed as the viewer would; null when it is not text.</summary>
    public static IReadOnlyList<FoundLine>? Read(Stream stream, string text, bool matchCase, bool wholeWords, bool regex, CancellationToken cancellationToken = default)
    {
        var sample = new byte[SampleBytes];
        var read = stream.ReadAtLeast(sample, sample.Length, throwOnEndOfStream: false);
        var span = sample.AsSpan(0, read);
        if (!TextEncoding.LooksLikeText(span))
        {
            return null;
        }

        stream.Position = 0;
        var pattern = regex
            ? TextFinder.Compile(text, matchCase, wholeWords)
            : new Regex(wholeWords ? $@"\b{Regex.Escape(text)}\b" : Regex.Escape(text),
                RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromSeconds(2));
        var lines = new List<FoundLine>();
        using var reader = new StreamReader(stream, TextEncoding.Detect(span).Encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        var number = 0;
        while (reader.ReadLine() is { } line)
        {
            number++;
            if ((number & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Match match;
            try
            {
                match = pattern.Match(line);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (!match.Success)
            {
                continue;
            }

            lines.Add(Shown(number, line, match.Index, Math.Max(1, match.Length)));
            if (lines.Count >= MaxPerFile)
            {
                break;
            }
        }

        return lines;
    }

    /// <summary>The line without its indent, cut around the match when it is long.</summary>
    internal static FoundLine Shown(int number, string line, int index, int length)
    {
        var start = 0;
        while (start < index && char.IsWhiteSpace(line[start]))
        {
            start++;
        }

        var end = line.Length;
        var prefix = string.Empty;
        if (end - start > MaxShown)
        {
            // Some context before the match, then as much as fits.
            var from = Math.Max(start, index - (MaxShown / 3));
            if (from > start)
            {
                prefix = "…";
                start = from;
            }

            end = Math.Min(line.Length, start + MaxShown);
        }

        var shown = new StringBuilder(prefix).Append(line.AsSpan(start, end - start)).Append(end < line.Length ? "…" : string.Empty).ToString();
        var matchStart = prefix.Length + index - start;
        var matchLength = Math.Min(length, Math.Max(0, shown.Length - matchStart));
        return new FoundLine(number, index + 1, shown, matchStart, matchLength);
    }
}
