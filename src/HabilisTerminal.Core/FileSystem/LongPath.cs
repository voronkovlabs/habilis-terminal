namespace HabilisTerminal.Core.FileSystem;

/// <summary>Helpers for paths handed to Win32 APIs that need the <c>\\?\</c> form beyond MAX_PATH (SPEC §8).</summary>
public static class LongPath
{
    public const string ExtendedPrefix = @"\\?\";
    public const string ExtendedUncPrefix = @"\\?\UNC\";

    /// <summary><c>C:\a</c> → <c>\\?\C:\a</c>; <c>\\server\share\a</c> → <c>\\?\UNC\server\share\a</c>.</summary>
    public static string ToExtended(string path)
    {
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? ExtendedUncPrefix + full[2..]
            : ExtendedPrefix + full;
    }

    /// <summary>The user-facing form: strips <c>\\?\</c> and <c>\\?\UNC\</c>.</summary>
    public static string ToDisplay(string path)
    {
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[ExtendedUncPrefix.Length..];
        }

        return path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) ? path[ExtendedPrefix.Length..] : path;
    }

    /// <summary>True for UNC paths, including the extended form.</summary>
    public static bool IsUnc(string path) =>
        path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase) ||
        (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal));
}
