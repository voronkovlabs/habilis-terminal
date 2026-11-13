using HabilisTerminal.Shell.ConPty;

namespace HabilisTerminal.Shell.Pty;

/// <summary>What to start inside a pseudo terminal.</summary>
/// <param name="FileName">The program.</param>
/// <param name="Arguments">Its arguments as one command line, quoted the Windows way (as the shell settings hold them).</param>
/// <param name="Environment">Variables added to (or overriding) the inherited environment.</param>
/// <param name="ArgumentList">Off Windows: the arguments one by one (argv after the program); Arguments is for ConPTY.</param>
/// <param name="ArgumentZero">Off Windows: argv[0] when it is not the program, e.g. "-zsh" for a login shell.</param>
public sealed record PtyStartInfo(
    string FileName,
    string Arguments,
    string? WorkingDirectory,
    int Columns,
    int Rows,
    IReadOnlyDictionary<string, string>? Environment = null,
    IReadOnlyList<string>? ArgumentList = null,
    string? ArgumentZero = null)
{
    /// <summary>The whole command line: the quoted program, then the arguments.</summary>
    public string CommandLine => string.IsNullOrEmpty(Arguments) ? $"\"{FileName}\"" : $"\"{FileName}\" {Arguments}";
}

/// <summary>A child process attached to a pseudo terminal: ConPTY on Windows, a pty elsewhere (#259).</summary>
public interface IPseudoTerminal : IDisposable
{
    int ProcessId { get; }

    /// <summary>Input for the child (keys, pasted text, replies to terminal queries).</summary>
    void Write(string text);

    void Resize(int columns, int rows);
}

public static class PseudoTerminal
{
    /// <param name="onOutput">Called on a background thread with decoded output (VT sequences included).</param>
    /// <param name="onExited">Called once on a background thread when the child process exits.</param>
    public static IPseudoTerminal Start(PtyStartInfo startInfo, Action<string> onOutput, Action<int> onExited) =>
        OperatingSystem.IsWindows()
            ? ConPtySession.Start(startInfo, onOutput, onExited)
            : UnixPseudoTerminal.Start(startInfo, onOutput, onExited);
}
