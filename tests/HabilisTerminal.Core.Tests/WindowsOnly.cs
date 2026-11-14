using System.Runtime.CompilerServices;

namespace HabilisTerminal.Core.Tests;

/// <summary>
/// A test of Windows behaviour (drive roots, junctions, cmd.exe, ACLs, names Windows forbids): skipped on other systems,
/// where the port (#253, #259) needs its own tests for the same features.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Windows behaviour";
        SkipUnless = nameof(WindowsOnly.IsWindows);
        SkipType = typeof(WindowsOnly);
    }
}

/// <inheritdoc cref="WindowsFactAttribute"/>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Windows behaviour";
        SkipUnless = nameof(WindowsOnly.IsWindows);
        SkipType = typeof(WindowsOnly);
    }
}

public static class WindowsOnly
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    public static bool IsUnix => !OperatingSystem.IsWindows();
}

/// <summary>A test of the same features on a Mac or Linux (#260): skipped on Windows.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Mac and Linux behaviour";
        SkipUnless = nameof(WindowsOnly.IsUnix);
        SkipType = typeof(WindowsOnly);
    }
}

/// <inheritdoc cref="UnixFactAttribute"/>
public sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Mac and Linux behaviour";
        SkipUnless = nameof(WindowsOnly.IsUnix);
        SkipType = typeof(WindowsOnly);
    }
}
