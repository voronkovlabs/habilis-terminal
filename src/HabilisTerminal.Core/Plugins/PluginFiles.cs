namespace HabilisTerminal.Core.Plugins;

/// <summary>Which file a plugin's <c>FileExtensions</c> claim, and where a location inside a file splits (issue #148).</summary>
public static class PluginFiles
{
    /// <summary>The name ends in one of the extensions: "logs.tar.gz" matches "gz" and "tar.gz"; "zip" alone is no match, nor is "azip".</summary>
    public static bool Claims(IEnumerable<string> extensions, string fileName) =>
        extensions.Any(e => e.Length > 0 && fileName.Length > e.Length + 1 &&
            fileName.EndsWith("." + e.TrimStart('.'), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A location like <c>C:\a.zip\docs\sub</c>: the longest part that is a file, and the rest inside it. Null when no
    /// part is a file.
    /// </summary>
    public static (string File, string InnerPath)? SplitLocation(string location, Func<string, bool> fileExists)
    {
        var file = location.TrimEnd('\\');
        while (file.Length > 3)
        {
            if (fileExists(file))
            {
                return (file, location.Length > file.Length ? location[file.Length..].Trim('\\') : string.Empty);
            }

            var cut = file.LastIndexOf('\\');
            if (cut <= 2)
            {
                return null;
            }

            file = file[..cut];
        }

        return null;
    }
}
