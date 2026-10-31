namespace HabilisTerminal.Shell;

/// <summary>Finds PowerShell 7+ (pwsh.exe) on this machine (SPEC §7).</summary>
public static class PwshLocator
{
    public const string ExecutableName = "pwsh.exe";

    public static string? Find()
    {
        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(dir, ExecutableName);
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "PowerShell", "7", ExecutableName);
        yield return Path.Combine(programFiles, "PowerShell", "7-preview", ExecutableName);

        // Store and winget installs expose an app execution alias here.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Microsoft", "WindowsApps", ExecutableName);
    }
}
