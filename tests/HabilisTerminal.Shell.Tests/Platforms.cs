using System.Runtime.CompilerServices;

namespace HabilisTerminal.Shell.Tests;

/// <summary>pwsh in ConPTY with the PowerShell integration: Windows only (issue #268).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Windows console";
        SkipUnless = nameof(Platforms.IsWindows);
        SkipType = typeof(Platforms);
    }
}

/// <summary>A pty and zsh: macOS and Linux (issue #268).</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "macOS and Linux console";
        SkipUnless = nameof(Platforms.IsUnixWithZsh);
        SkipType = typeof(Platforms);
    }
}

public static class Platforms
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    public static bool IsUnixWithZsh => !OperatingSystem.IsWindows() && File.Exists("/bin/zsh");
}
