using System.Globalization;
using System.Text;

namespace Habilis.Aws.S3;

/// <summary>
/// Object keys as panel names and as local file names. S3 allows any character in a key; Windows does not allow
/// <c>&lt;&gt;:"|?*\</c>, control characters, trailing dots and spaces, or device names such as CON and NUL. Those are
/// percent-encoded on download ("what?.txt" becomes "what%3F.txt"); everything else is kept as it is. A "%" that
/// would read as an escape is encoded too, so two different keys never map to the same local name (apart from
/// letter case, which Windows ignores: the download reports such clashes).
/// </summary>
internal static class KeyNames
{
    /// <summary>
    /// The name of an empty path segment ("a//b" has a folder with an empty name under "a/"), in the panel and on
    /// disk. "%2F" is the encoded "/" that ends the segment, as the AWS console shows such a folder as "/".
    /// </summary>
    public const string EmptySegment = "%2F";

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A key segment as the panel shows it: exactly, except an empty one.</summary>
    public static string DisplayName(string segment) => segment.Length == 0 ? EmptySegment : segment;

    /// <summary>A key segment (no "/") as a valid Windows file name.</summary>
    public static string LocalName(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Length == 0)
        {
            return EmptySegment;
        }

        // Windows drops trailing dots and spaces, so all of them are encoded.
        var end = segment.Length;
        while (end > 0 && segment[end - 1] is '.' or ' ')
        {
            end--;
        }

        var text = new StringBuilder(segment.Length + 8);
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            var encode = i >= end
                || c < ' '
                || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '\\' or '/'
                || (c == '%' && i + 2 < segment.Length && char.IsAsciiHexDigit(segment[i + 1]) && char.IsAsciiHexDigit(segment[i + 2]));
            if (encode)
            {
                text.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                text.Append(c);
            }
        }

        // "CON", "nul.txt", "COM1 .log": the base name before the first dot is a device name. Encoding its last
        // letter is enough to make it an ordinary name.
        var name = text.ToString();
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot >= 0 ? name[..dot] : name).TrimEnd(' ');
        if (DeviceNames.Contains(stem))
        {
            var last = stem.Length - 1;
            name = string.Concat(name.AsSpan(0, last), "%", ((int)stem[last]).ToString("X2", CultureInfo.InvariantCulture), name.AsSpan(last + 1));
        }

        return name;
    }

    /// <summary>A key relative to a folder ("sub/dir/file.txt") as a relative local path ("sub\dir\file.txt").</summary>
    public static string LocalPath(string relativeKey) =>
        string.Join(Path.DirectorySeparatorChar, relativeKey.Split('/').Select(LocalName));

    /// <summary>
    /// The name of a child under <paramref name="prefix"/>: an object's key or a common prefix (whose one trailing
    /// "/" is not part of the name).
    /// </summary>
    public static string ChildSegment(string prefix, string keyOrPrefix, bool isFolder)
    {
        var rest = keyOrPrefix[prefix.Length..];
        return isFolder && rest.EndsWith('/') ? rest[..^1] : rest;
    }

    /// <summary>The parent of a folder prefix: "a/b/" → "a/", "a//" → "a/", "a/" → "".</summary>
    public static string ParentPrefix(string prefix)
    {
        if (prefix.Length == 0)
        {
            return string.Empty;
        }

        var trimmed = prefix[..^1];
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? string.Empty : trimmed[..(slash + 1)];
    }

    /// <summary>A relative local path ("sub\file.txt") as a key suffix ("sub/file.txt").</summary>
    public static string KeyOf(string relativePath) => relativePath.Replace(Path.DirectorySeparatorChar, '/');
}
