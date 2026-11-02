namespace HabilisTerminal.Core.FileSystem;

/// <summary>
/// Enter on a file (issue #52): programs and scripts run in the shell; any other file opens in its associated program,
/// as a double click in Explorer would. A program is an extension from <c>PATHEXT</c>, plus PowerShell scripts.
/// </summary>
public static class RunnableFiles
{
    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC";

    /// <param name="pathExt">The <c>PATHEXT</c> value; null or empty uses the Windows default.</param>
    public static bool IsRunnable(string name, string? pathExt)
    {
        var extension = Path.GetExtension(name);
        if (extension.Length < 2)
        {
            return false;
        }

        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var list = string.IsNullOrWhiteSpace(pathExt) ? DefaultPathExt : pathExt;
        return list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsRunnable(string name) => IsRunnable(name, Environment.GetEnvironmentVariable("PATHEXT"));
}
