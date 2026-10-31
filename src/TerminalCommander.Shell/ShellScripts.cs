namespace TerminalCommander.Shell;

public static class ShellScripts
{
    /// <summary>The pwsh integration script deployed next to the executable.</summary>
    public static string IntegrationScriptPath => Path.Combine(AppContext.BaseDirectory, "shell", "TerminalCommander.ps1");
}
