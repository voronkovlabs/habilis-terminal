using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static TerminalCommander.Shell.ConPty.ConPtyNative;

namespace TerminalCommander.Shell.ConPty;

/// <summary>
/// A child process attached to a pseudo console. Created with the same flags as Windows Terminal
/// (resize quirk and win32-input-mode), so the terminal control owns reflow on resize and receives
/// full key records.
/// </summary>
public sealed class ConPtySession : IDisposable
{
    private readonly object _sync = new();
    private IntPtr _pseudoConsole;
    private FileStream? _input;
    private FileStream? _output;
    private Process? _process;
    private bool _disposed;

    private ConPtySession()
    {
    }

    /// <summary>Raised on a background thread with decoded output text (VT sequences included).</summary>
    public event Action<string>? Output;

    /// <summary>Raised on a background thread when the child process exits.</summary>
    public event Action<int>? Exited;

    public int ProcessId { get; private set; }

    public static ConPtySession Start(string commandLine, string? workingDirectory, int columns, int rows)
    {
        var session = new ConPtySession();
        try
        {
            session.StartCore(commandLine, workingDirectory, columns, rows);
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

            input.Write(bytes);
            input.Flush();
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

    private void StartCore(string commandLine, string? workingDirectory, int columns, int rows)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0) ||
            !CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create pseudo console pipes.");
        }

        var size = new COORD { X = (short)Math.Max(1, columns), Y = (short)Math.Max(1, rows) };
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

        var processInfo = CreateAttachedProcess(commandLine, workingDirectory);
        ProcessId = processInfo.dwProcessId;
        _process = Process.GetProcessById(processInfo.dwProcessId);
        CloseHandle(processInfo.hThread);
        CloseHandle(processInfo.hProcess);

        var readThread = new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY output" };
        readThread.Start();

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Exited?.Invoke(SafeExitCode(_process));
        if (_process.HasExited)
        {
            Exited?.Invoke(SafeExitCode(_process));
        }
    }

    private PROCESS_INFORMATION CreateAttachedProcess(string commandLine, string? workingDirectory)
    {
        var attributeListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
        var attributeList = Marshal.AllocHGlobal(attributeListSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize) ||
                !UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _pseudoConsole, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the pseudo console.");
            }

            var startupInfo = new STARTUPINFOEX { lpAttributeList = attributeList };
            startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

            var commandLineBuffer = (commandLine + '\0').ToCharArray();
            if (!CreateProcess(null, commandLineBuffer, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero,
                    workingDirectory, ref startupInfo, out var processInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start '{commandLine}'.");
            }

            return processInfo;
        }
        finally
        {
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
        }
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
                    Output?.Invoke(new string(chars, 0, count));
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
