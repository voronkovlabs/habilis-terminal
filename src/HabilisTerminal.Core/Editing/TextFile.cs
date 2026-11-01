using HabilisTerminal.Core.Safety;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.Core.Editing;

public enum LineEnding
{
    CrLf,
    Lf,
    Cr,
}

/// <summary>
/// A text file loaded for editing (SPEC §9): the text plus everything needed to write it back byte-identically —
/// encoding, BOM and the dominant line ending (used for new lines; existing ones are kept as they are).
/// </summary>
public sealed record TextFile(string Path, string Text, TextEncoding Encoding, bool HasBom, LineEnding LineEnding, DateTime LastWriteTimeUtc, long Length)
{
    public string NewLine => LineEnding switch
    {
        LineEnding.Lf => "\n",
        LineEnding.Cr => "\r",
        _ => "\r\n",
    };

    public static TextFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var info = new FileInfo(path);
        var encoding = TextEncoding.Detect(bytes);
        var bomLength = encoding.DetectedBom ? encoding.BomLength : 0;
        var text = encoding.Encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        return new TextFile(path, text, encoding, encoding.DetectedBom, DominantLineEnding(text), info.LastWriteTimeUtc, info.Length);
    }

    /// <summary>Re-reads the bytes with another encoding (Shift+F8 before editing).</summary>
    public TextFile Reinterpret(TextEncoding encoding)
    {
        var bytes = File.ReadAllBytes(Path);
        var bom = HasBom && encoding.BomLength > 0 && encoding.UnitSize == Encoding.UnitSize ? encoding.BomLength : 0;
        var text = encoding.Encoding.GetString(bytes, bom, bytes.Length - bom);
        return this with { Text = text, Encoding = encoding, HasBom = bom > 0, LineEnding = DominantLineEnding(text) };
    }

    /// <summary>True when the file on disk changed since it was loaded or last saved.</summary>
    public bool ChangedOnDisk()
    {
        var info = new FileInfo(Path);
        return info.Exists && (info.LastWriteTimeUtc != LastWriteTimeUtc || info.Length != Length);
    }

    /// <summary>
    /// Writes <paramref name="text"/> atomically: a temporary file next to the target, then <c>ReplaceFile</c>, which
    /// keeps the target's attributes, ACL and creation time. Returns the state after saving.
    /// </summary>
    /// <param name="path">Target; another path means "save as".</param>
    public TextFile Save(string text, string? path = null, TextEncoding? encoding = null, bool? withBom = null)
    {
        var target = System.IO.Path.GetFullPath(path ?? Path);
        DestructiveOperationGuard.Check(target);
        var targetEncoding = encoding ?? Encoding;
        var bom = withBom ?? (HasBom && targetEncoding.BomLength > 0);
        var directory = System.IO.Path.GetDirectoryName(target)!;
        var temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(target)}.{Guid.NewGuid():N}.habi-tmp");

        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (bom)
            {
                stream.Write(Preamble(targetEncoding));
            }

            stream.Write(targetEncoding.Encoding.GetBytes(text));
        }

        try
        {
            if (File.Exists(target))
            {
                File.Replace(temporary, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, target);
            }
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }

        var info = new FileInfo(target);
        return this with { Path = target, Text = text, Encoding = targetEncoding, HasBom = bom, LineEnding = DominantLineEnding(text), LastWriteTimeUtc = info.LastWriteTimeUtc, Length = info.Length };
    }

    public static LineEnding DominantLineEnding(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }

        if (crlf == 0 && lf == 0 && cr == 0)
        {
            return Environment.NewLine == "\n" ? LineEnding.Lf : LineEnding.CrLf;
        }

        return lf > crlf && lf >= cr ? LineEnding.Lf : cr > crlf && cr > lf ? LineEnding.Cr : LineEnding.CrLf;
    }

    private static byte[] Preamble(TextEncoding encoding) => encoding.Name switch
    {
        var name when name == TextEncoding.Utf8.Name => [0xEF, 0xBB, 0xBF],
        var name when name == TextEncoding.Utf16LittleEndian.Name => [0xFF, 0xFE],
        var name when name == TextEncoding.Utf16BigEndian.Name => [0xFE, 0xFF],
        _ => [],
    };
}
