namespace HabilisTerminal.Core.FileSystem;

/// <summary>
/// Separators of the system's paths (#259): '\' on Windows, as the code always assumed, '/' elsewhere. On Windows the
/// results are what the old <c>TrimEnd('\\')</c> and <c>+ '\\'</c> gave.
/// </summary>
public static class PathText
{
    public static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

    extension(string path)
    {
        /// <summary>Without trailing separators; a Unix root ("/") stays itself. On Windows, <c>C:\</c> gives <c>C:</c> as before.</summary>
        public string TrimEndSeparator() =>
            OperatingSystem.IsWindows() ? path.TrimEnd('\\')
            : path.Length > 1 ? path.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : "/"
            : path;

        /// <summary>With exactly one trailing separator.</summary>
        public string WithEndSeparator() =>
            OperatingSystem.IsWindows() ? path.TrimEnd('\\') + '\\' : path.TrimEnd('/') + '/';
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or lies inside it (case-insensitive, as NTFS and APFS are by default).</summary>
    public static bool IsSameOrInside(string path, string root) =>
        Path.GetFullPath(path).WithEndSeparator().StartsWith(Path.GetFullPath(root).WithEndSeparator(), StringComparison.OrdinalIgnoreCase);
}
