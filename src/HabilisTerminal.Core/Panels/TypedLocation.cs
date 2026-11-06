namespace HabilisTerminal.Core.Panels;

/// <summary>A place pasted into a menu's filter (issue #142): a local or network path, or a plugin's address.</summary>
public static class TypedLocation
{
    /// <summary>Without surrounding spaces and the quotes Explorer's "Copy as path" adds.</summary>
    public static string Clean(string text)
    {
        var clean = text.Trim();
        return clean.Length >= 2 && clean[0] == clean[^1] && clean[0] is '"' or '\'' ? clean[1..^1].Trim() : clean;
    }

    /// <summary>A full path (<c>C:\x</c>, <c>C:/x</c>, <c>\\server\share</c>); what only names something, or a bare <c>C:</c>, is not.</summary>
    public static bool IsPath(string text) => Path.IsPathFullyQualified(ToWindowsPath(text));

    public static string ToWindowsPath(string text) => text.Replace('/', '\\');

    /// <summary>
    /// What <c>goto</c> in the console means (issue #152). The same as a paste into the drive menu, and as it is typed by
    /// hand in a shell, also relative to the shell's folder, with <c>~</c> and <c>%VARIABLES%</c>.
    /// </summary>
    /// <param name="isAddress">An address some plugin registered: it stays as it is.</param>
    public static string ForGoto(string text, string currentFolder, Func<string, bool> isAddress)
    {
        var clean = Environment.ExpandEnvironmentVariables(Clean(text));
        if (isAddress(clean))
        {
            return clean;
        }

        if (clean == "~" || clean.StartsWith("~\\", StringComparison.Ordinal) || clean.StartsWith("~/", StringComparison.Ordinal))
        {
            clean = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + clean[1..];
        }

        return IsPath(clean) ? clean : Path.GetFullPath(ToWindowsPath(clean), currentFolder);
    }
}
