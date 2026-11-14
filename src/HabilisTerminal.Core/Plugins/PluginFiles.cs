namespace HabilisTerminal.Core.Plugins;

/// <summary>Which file a plugin's <c>FileExtensions</c> claim, and where a location inside a file splits (issue #148).</summary>
public static class PluginFiles
{
    /// <summary>The name ends in one of the extensions: "logs.tar.gz" matches "gz" and "tar.gz"; "zip" alone is no match, nor is "azip".</summary>
    public static bool Claims(IEnumerable<string> extensions, string fileName) =>
        extensions.Any(e => e.Length > 0 && fileName.Length > e.Length + 1 &&
            fileName.EndsWith("." + e.TrimStart('.'), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A location like <c>C:\a.zip\docs\sub</c> (on a Mac <c>/data/a.zip/docs/sub</c>): the longest part that is a file,
    /// and the rest inside it, with backslashes as plugins take it (#260). Null when no part is a file.
    /// </summary>
    public static (string File, string InnerPath)? SplitLocation(string location, Func<string, bool> fileExists)
    {
        var windows = OperatingSystem.IsWindows();
        var separator = windows ? '\\' : '/';
        var root = windows ? 3 : 1; // "C:\" or "/"
        var file = location.TrimEnd(separator);
        while (file.Length > root)
        {
            if (fileExists(file))
            {
                var inner = location.Length > file.Length ? location[file.Length..].Trim(separator) : string.Empty;
                return (file, inner.Replace(separator, '\\'));
            }

            var cut = file.LastIndexOf(separator);
            if (cut < root)
            {
                return null;
            }

            file = file[..cut];
        }

        return null;
    }
}
