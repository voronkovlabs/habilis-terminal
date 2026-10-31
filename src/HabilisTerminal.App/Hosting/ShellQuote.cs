namespace HabilisTerminal.App.Hosting;

/// <summary>Quoting for text inserted into a PowerShell command line.</summary>
public static class ShellQuote
{
    /// <summary>Leaves simple names as they are; anything else goes into single quotes with <c>'</c> doubled.</summary>
    public static string Quote(string text)
    {
        var simple = text.Length > 0 && text.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '\\' or ':' or '/');
        return simple ? text : "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
