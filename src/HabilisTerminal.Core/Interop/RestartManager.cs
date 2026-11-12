using HabilisTerminal.Core.Platform;

namespace HabilisTerminal.Core.Interop;

/// <summary>Which processes keep a file open (Windows Restart Manager), through <see cref="Platforms.Current"/>.</summary>
public static class RestartManager
{
    public static string? LockingProcesses(string path) => Platforms.Current.FileLocks.LockingProcesses(path);
}
