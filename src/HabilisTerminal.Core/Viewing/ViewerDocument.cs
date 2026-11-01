using System.IO.MemoryMappedFiles;
using System.Text;

namespace HabilisTerminal.Core.Viewing;

/// <summary>
/// A read-only, memory-mapped view of a file of any size (SPEC §9). Positions are byte offsets: there is no line
/// index, so a multi-gigabyte log opens instantly; lines are found by scanning around the current position.
/// The file stays readable and writable by others (growing logs).
/// </summary>
public sealed class ViewerDocument : IDisposable
{
    /// <summary>Lines longer than this are split for display, so a single huge line cannot stall rendering.</summary>
    public const int MaxLineBytes = 16 * 1024;

    private const int SearchChunk = 1 << 20;

    private readonly MemoryMappedFile? _map;
    private readonly MemoryMappedViewAccessor? _view;

    private ViewerDocument(string path, MemoryMappedFile? map, MemoryMappedViewAccessor? view, long length, TextEncoding encoding)
    {
        Path = path;
        _map = map;
        _view = view;
        Length = length;
        Encoding = encoding;
    }

    public string Path { get; }

    public long Length { get; }

    public TextEncoding Encoding { get; set; }

    /// <summary>First byte after the BOM of the current encoding.</summary>
    public long ContentStart => Encoding.BomLength <= Length && HasBom(Encoding) ? Encoding.BomLength : 0;

    public static ViewerDocument Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        if (length == 0)
        {
            stream.Dispose();
            return new ViewerDocument(path, null, null, 0, TextEncoding.Utf8);
        }

        var map = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        var view = map.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
        var sample = new byte[(int)Math.Min(length, 64 * 1024)];
        view.ReadArray(0, sample, 0, sample.Length);
        return new ViewerDocument(path, map, view, length, TextEncoding.Detect(sample));
    }

    public byte ReadByte(long offset) => _view!.ReadByte(offset);

    public int Read(long offset, byte[] buffer, int count)
    {
        if (_view is null || offset >= Length)
        {
            return 0;
        }

        var read = (int)Math.Min(count, Length - offset);
        _view.ReadArray(offset, buffer, 0, read);
        return read;
    }

    /// <summary>The start of the line after the one starting at <paramref name="offset"/>, or <see cref="Length"/>.</summary>
    public long NextLineStart(long offset)
    {
        var unit = Encoding.UnitSize;
        var limit = Math.Min(Length, offset + MaxLineBytes);
        for (var position = offset; position + unit <= limit; position += unit)
        {
            var c = ReadUnit(position);
            if (c == '\n')
            {
                return position + unit;
            }

            if (c == '\r')
            {
                return position + unit + unit <= Length && ReadUnit(position + unit) == '\n' ? position + (2 * unit) : position + unit;
            }
        }

        // Very long line: break it for display, keeping UTF-16 alignment.
        return limit >= Length ? Length : AlignDown(limit, offset);
    }

    /// <summary>The start of the line before the one starting at <paramref name="offset"/>.</summary>
    public long PreviousLineStart(long offset)
    {
        var start = ContentStart;
        if (offset <= start)
        {
            return start;
        }

        var unit = Encoding.UnitSize;

        // Skip the line break that ends the previous line.
        var position = offset - unit;
        if (position >= start && ReadUnit(position) == '\n')
        {
            position -= unit;
            if (position >= start && ReadUnit(position) == '\r')
            {
                position -= unit;
            }
        }
        else if (position >= start && ReadUnit(position) == '\r')
        {
            position -= unit;
        }

        var limit = Math.Max(start, offset - MaxLineBytes);
        for (; position >= limit; position -= unit)
        {
            var c = ReadUnit(position);
            if (c is '\n' or '\r')
            {
                return position + unit;
            }
        }

        return limit == start ? start : AlignDown(limit, start);
    }

    /// <summary>The start of the line containing <paramref name="offset"/> (for "go to offset" and search hits).</summary>
    public long LineStartOf(long offset)
    {
        offset = AlignDown(Math.Clamp(offset, ContentStart, Length), ContentStart);
        var unit = Encoding.UnitSize;
        var limit = Math.Max(ContentStart, offset - MaxLineBytes);
        for (var position = offset - unit; position >= limit; position -= unit)
        {
            if (ReadUnit(position) is '\n' or '\r')
            {
                return position + unit;
            }
        }

        return limit;
    }
    /// <summary>The text of the line starting at <paramref name="offset"/> without its line break.</summary>
    public string ReadLine(long offset, out long next)
    {
        next = NextLineStart(offset);
        var byteCount = (int)(next - offset);
        if (byteCount <= 0)
        {
            return string.Empty;
        }

        var bytes = new byte[byteCount];
        Read(offset, bytes, byteCount);
        var text = Encoding.Encoding.GetString(bytes);
        return text.TrimEnd('\r', '\n');
    }

    /// <summary>Line number (1-based) of <paramref name="offset"/>; counts line breaks, so it scans the file up to there.</summary>
    public long LineNumberOf(long offset, CancellationToken cancellationToken = default)
    {
        long line = 1;
        var unit = Encoding.UnitSize;
        var buffer = new byte[SearchChunk];
        for (var position = ContentStart; position < offset; position += buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Read(position, buffer, (int)Math.Min(buffer.Length, offset - position));
            for (var i = 0; i + unit <= read; i += unit)
            {
                if (unit == 1 ? buffer[i] == '\n' : (Encoding.BigEndian ? buffer[i] == 0 && buffer[i + 1] == '\n' : buffer[i] == '\n' && buffer[i + 1] == 0))
                {
                    line++;
                }
            }
        }

        return line;
    }

    /// <summary>The offset of the start of line <paramref name="lineNumber"/> (1-based), or the end.</summary>
    public long OffsetOfLine(long lineNumber, CancellationToken cancellationToken = default)
    {
        var position = ContentStart;
        for (long line = 1; line < lineNumber && position < Length; line++)
        {
            if ((line & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            position = NextLineStart(position);
        }

        return position;
    }

    /// <summary>
    /// Finds <paramref name="text"/> starting at <paramref name="from"/> (forward) or before it (backward).
    /// Chunks are decoded whole and searched with ordinal comparison (vectorized); the byte offset is computed only
    /// for the hit. Chunks overlap, so matches across chunk borders are found.
    /// </summary>
    public long? Find(string text, long from, bool forward, bool matchCase, CancellationToken cancellationToken = default)
    {
        if (text.Length == 0 || Length == 0)
        {
            return null;
        }

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var overlap = (long)Encoding.Encoding.GetMaxByteCount(text.Length);
        var chunk = (long)SearchChunk;
        if (forward)
        {
            for (var start = CharBoundary(Math.Max(from, ContentStart)); start < Length; start = CharBoundary(start + chunk))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (decoded, bytes) = Decode(start, (int)Math.Min(chunk + overlap, Length - start));
                var index = decoded.IndexOf(text, comparison);
                if (index >= 0)
                {
                    return start + ByteCount(bytes, decoded, index);
                }
            }
        }
        else
        {
            for (var end = Math.Min(from, Length); end > ContentStart; end -= chunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = CharBoundary(Math.Max(ContentStart, end - chunk - overlap));
                var (decoded, bytes) = Decode(start, (int)(Math.Min(Length, end + overlap) - start));
                var index = decoded.LastIndexOf(text, comparison);
                while (index >= 0 && start + ByteCount(bytes, decoded, index) >= from)
                {
                    index = index == 0 ? -1 : decoded.LastIndexOf(text, index - 1, comparison);
                }

                if (index >= 0)
                {
                    return start + ByteCount(bytes, decoded, index);
                }
            }
        }

        return null;
    }
    public void Dispose()
    {
        _view?.Dispose();
        _map?.Dispose();
    }

    private static bool HasBom(TextEncoding encoding) => encoding.BomLength > 0 && encoding.DetectedBom;

    private char ReadUnit(long position) => Encoding.UnitSize switch
    {
        1 => (char)ReadByte(position),
        _ => Encoding.BigEndian ? (char)((ReadByte(position) << 8) | ReadByte(position + 1)) : (char)(ReadByte(position) | (ReadByte(position + 1) << 8)),
    };

    private long AlignDown(long position, long origin) =>
        Encoding.UnitSize == 1 ? position : origin + ((position - origin) / Encoding.UnitSize * Encoding.UnitSize);

    private (string Text, byte[] Bytes) Decode(long start, int count)
    {
        var bytes = new byte[count];
        var read = Read(start, bytes, count);
        return (Encoding.Encoding.GetString(bytes, 0, read), bytes);
    }

    /// <summary>Bytes taken by the first <paramref name="chars"/> characters of a decoded chunk.</summary>
    private int ByteCount(byte[] bytes, string decoded, int chars) =>
        Encoding.Encoding.GetByteCount(decoded.AsSpan(0, chars));

    /// <summary>Moves a position forward to the start of a character (UTF-8 continuation bytes, UTF-16 alignment).</summary>
    private long CharBoundary(long position)
    {
        position = AlignDown(Math.Clamp(position, ContentStart, Length), ContentStart);
        if (Encoding.Encoding is UTF8Encoding)
        {
            while (position < Length && (ReadByte(position) & 0xC0) == 0x80)
            {
                position++;
            }
        }

        return position;
    }
}