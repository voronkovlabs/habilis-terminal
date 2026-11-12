using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using HabilisTerminal.Shell.Pty;
using static HabilisTerminal.Shell.ConPty.ConPtyNative;

namespace HabilisTerminal.Shell.ConPty;

/// <summary>
/// A child process attached to a pseudo console. Created with the same flags as Windows Terminal
/// (resize quirk and win32-input-mode), so the terminal control owns reflow on resize and receives
/// full key records.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConPtySession : IPseudoTerminal
{
    private readonly object _sync = new();
    private readonly Action<string> _onOutput;
    private readonly Action<int> _onExited;
    private IntPtr _pseudoConsole;
    private FileStream? _input;
    private FileStream? _output;
    private Process? _process;
    private int _exitReported;
    private bool _disposed;

    private ConPtySession(Action<string> onOutput, Action<int> onExited)
    {
        _onOutput = onOutput;
        _onExited = onExited;
    }

    public int ProcessId { get; private set; }

    /// <param name="onOutput">Called on a background thread with decoded output (VT sequences included).</param>
    /// <param name="onExited">Called once on a background thread when the child process exits.</param>
    public static ConPtySession Start(PtyStartInfo startInfo, Action<string> onOutput, Action<int> onExited)
    {
        var session = new ConPtySession(onOutput, onExited);
        try
        {
            session.StartCore(startInfo);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public void Write(string text)
    {
        var input = _input;
        if (input is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                input.Write(bytes);
                input.Flush();
            }
            catch (IOException)
            {
                // The pseudo console is gone; the exit callback reports it.
            }
        }
    }

    public void Resize(int columns, int rows)
    {
        lock (_sync)
        {
            if (_disposed || _pseudoConsole == IntPtr.Zero || columns <= 0 || rows <= 0)
            {
                return;
            }

            _ = ResizePseudoConsole(_pseudoConsole, new COORD { X = (short)columns, Y = (short)rows });
        }
    }

    public void Dispose()
    {
        IntPtr pseudoConsole;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pseudoConsole = _pseudoConsole;
            _pseudoConsole = IntPtr.Zero;
        }

        _input?.Dispose();

        // ClosePseudoConsole may block until the output pipe is drained; the read loop keeps draining it.
        if (pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(pseudoConsole);
        }

        _output?.Dispose();
        _process?.Dispose();
    }

    private void StartCore(PtyStartInfo startInfo)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0) ||
            !CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create pseudo console pipes.");
        }

        var size = new COORD { X = (short)Math.Max(1, startInfo.Columns), Y = (short)Math.Max(1, startInfo.Rows) };
        var hr = CreatePseudoConsole(size, inputRead, outputWrite, PSEUDOCONSOLE_RESIZE_QUIRK | PSEUDOCONSOLE_WIN32_INPUT_MODE, out _pseudoConsole);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        // The pseudo console duplicated these; our copies must be closed so EOF propagates.
        inputRead.Dispose();
        outputWrite.Dispose();

        _input = new FileStream(inputWrite, FileAccess.Write, 1);
        _output = new FileStream(outputRead, FileAccess.Read, 1);

        var processInfo = CreateAttachedProcess(startInfo);
        ProcessId = processInfo.dwProcessId;
        _process = Process.GetProcessById(processInfo.dwProcessId);
        CloseHandle(processInfo.hThread);
        CloseHandle(processInfo.hProcess);

        var readThread = new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY output" };
        readThread.Start();

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => ReportExit();
        if (_process.HasExited)
        {
            ReportExit();
        }
    }

    private void ReportExit()
    {
        if (Interlocked.Exchange(ref _exitReported, 1) == 0 && _process is { } process)
        {
            _onExited(SafeExitCode(process));
        }
    }

    private PROCESS_INFORMATION CreateAttachedProcess(PtyStartInfo startInfo)
    {
        var attributeListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
        var attributeList = Marshal.AllocHGlobal(attributeListSize);
        var environmentBlock = BuildEnvironmentBlock(startInfo.Environment);
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize) ||
                !UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _pseudoConsole, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the pseudo console.");
            }

            var startupInfo = new STARTUPINFOEX { lpAttributeList = attributeList };
            startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

            // Empty std handles: otherwise a parent that has a console (tests, a console host)
            // passes its own handles and the child writes past the pseudo console.
            startupInfo.StartupInfo.dwFlags = STARTF_USESTDHANDLES;

            var commandLineBuffer = (startInfo.CommandLine + '\0').ToCharArray();
            if (!CreateProcess(null, commandLineBuffer, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, environmentBlock,
                    startInfo.WorkingDirectory, ref startupInfo, out var processInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start '{startInfo.CommandLine}'.");
            }

            return processInfo;
        }
        finally
        {
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }

    /// <summary>Inherited environment plus overrides, as a sorted Unicode block; zero means "inherit as is".</summary>
    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return IntPtr.Zero;
        }

        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            variables[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        }

        foreach (var (name, value) in overrides)
        {
            variables[name] = value;
        }

        var block = new StringBuilder();
        foreach (var (name, value) in variables)
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }

        block.Append('\0');
        return Marshal.StringToHGlobalUni(block.ToString());
    }

    private void ReadLoop()
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            while (_output is { } output)
            {
                var read = output.Read(bytes, 0, bytes.Length);
                if (read == 0)
                {
                    break;
                }

                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                if (count > 0)
                {
                    _onOutput(new string(chars, 0, count));
                }
            }
        }
        catch (IOException)
        {
            // Pipe closed: the pseudo console is gone.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }
}
