namespace HabilisTerminal.App.Hosting;

/// <summary>
/// Quoting for text inserted into the console's command line: PowerShell's on Windows, a POSIX shell's (zsh) on macOS
/// and Linux (issue #268).
/// </summary>
public static class ShellQuote
{
    /// <summary>Leaves simple names as they are; anything else goes into single quotes.</summary>
    public static string Quote(string text) => Quote(text, posix: !OperatingSystem.IsWindows());

    /// <summary>Runs a program of the shell's folder: <c>&amp; '.\name'</c> in PowerShell, <c>./name</c> in zsh.</summary>
    public static string RunHere(string name) =>
        OperatingSystem.IsWindows() ? "& " + Quote(".\\" + name, posix: false) : Quote("./" + name, posix: true);

    internal static string Quote(string text, bool posix)
    {
        if (posix)
        {
            // In single quotes nothing is special but the quote itself, which ends them: 'it'\''s'.
            var plain = text.Length > 0 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/' or '+' or ',' or '@' or '%');
            return plain ? text : "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        }

        var simple = text.Length > 0 && text.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '\\' or ':' or '/');
        return simple ? text : "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
