using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static HabilisTerminal.Shell.ConPty.ConPtyNative;

namespace HabilisTerminal.Shell.ConPty;

/// <summary>What to start inside a pseudo console.</summary>
/// <param name="Environment">Variables added to (or overriding) the inherited environment.</param>
public sealed record ConPtyStartInfo(
    string CommandLine,
    string? WorkingDirectory,
    int Columns,
    int Rows,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>The handles of a pseudo console, as numbers valid in the process they were made for, and what was read of its output but not given out (issue #122).</summary>
public sealed record ConPtyTransfer(long Input, long Output, long Signal, long Reference, long Server, int ShellProcessId, string Pending);

/// <summary>
/// A child process attached to a pseudo console. Created with the same flags as Windows Terminal
/// (resize quirk and win32-input-mode), so the terminal control owns reflow on resize and receives
/// full key records.
/// </summary>
public sealed class ConPtySession : IDisposable
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
    private Thread? _reader;
    private uint _readerThreadId;
    private volatile bool _stopReading;
    private byte[] _pending = [];
    private bool _handedOff;

    private ConPtySession(Action<string> onOutput, Action<int> onExited)
    {
        _onOutput = onOutput;
        _onExited = onExited;
    }

    public int ProcessId { get; private set; }

    /// <param name="onOutput">Called on a background thread with decoded output (VT sequences included).</param>
    /// <param name="onExited">Called once on a background thread when the child process exits.</param>
    public static ConPtySession Start(ConPtyStartInfo startInfo, Action<string> onOutput, Action<int> onExited)
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

    private void StartCore(ConPtyStartInfo startInfo)
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

        StartReader();
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => ReportExit();
        if (_process.HasExited)
        {
            ReportExit();
        }
    }

    private void StartReader()
    {
        _stopReading = false;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY output" };
        _reader.Start();
    }

    /// <summary>
    /// The first half of a move to another process (issue #122): reading stops, nothing read is lost, and the console's
    /// handles are duplicated into the target. The console goes on here until <see cref="CompleteHandoff"/>.
    /// </summary>
    public ConPtyTransfer PrepareHandoff(IntPtr targetProcess)
    {
        StopReader();

        // HPCON's layout in the conpty.dll we ship (Microsoft.Windows.Console.ConPTY): signal, reference, server process.
        var signal = Marshal.ReadIntPtr(_pseudoConsole, 0);
        var reference = Marshal.ReadIntPtr(_pseudoConsole, IntPtr.Size);
        var server = Marshal.ReadIntPtr(_pseudoConsole, 2 * IntPtr.Size);
        if (GetProcessId(server) == 0)
        {
            StartReader();
            throw new InvalidOperationException("The pseudo console's handles are not where they are expected.");
        }

        long Duplicate(IntPtr handle)
        {
            if (!DuplicateHandle(GetCurrentProcess(), handle, targetProcess, out var copy, 0, false, DUPLICATE_SAME_ACCESS))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not duplicate a pseudo console handle.");
            }

            return copy.ToInt64();
        }

        return new ConPtyTransfer(
            Duplicate(_input!.SafeFileHandle.DangerousGetHandle()),
            Duplicate(_output!.SafeFileHandle.DangerousGetHandle()),
            Duplicate(signal),
            Duplicate(reference),
            Duplicate(server),
            ProcessId,
            Convert.ToBase64String(_pending));
    }

    /// <summary>The new process has taken over: let go without ending the console, which ClosePseudoConsole would do.</summary>
    public void CompleteHandoff()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _handedOff = true;
            _ = CloseHandle(Marshal.ReadIntPtr(_pseudoConsole, 0));
            _ = CloseHandle(Marshal.ReadIntPtr(_pseudoConsole, IntPtr.Size));
            _ = CloseHandle(Marshal.ReadIntPtr(_pseudoConsole, 2 * IntPtr.Size));
            _pseudoConsole = IntPtr.Zero;
        }

        _input?.Dispose();
        _output?.Dispose();
        _process?.Dispose();
    }

    /// <summary>The move failed: the console goes on here. Copies in a process that is still alive stay there unused.</summary>
    public void CancelHandoff() => StartReader();

    /// <summary>Takes over a console handed over by another process, and reads on from where it stopped.</summary>
    public static ConPtySession Adopt(ConPtyTransfer transfer, Action<string> onOutput, Action<int> onExited)
    {
        var session = new ConPtySession(onOutput, onExited);
        var hr = ConptyPackPseudoConsole(new IntPtr(transfer.Server), new IntPtr(transfer.Reference), new IntPtr(transfer.Signal), out session._pseudoConsole);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        session._input = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(transfer.Input), true), FileAccess.Write, 1);
        session._output = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(transfer.Output), true), FileAccess.Read, 1);
        session._pending = Convert.FromBase64String(transfer.Pending);
        session.ProcessId = transfer.ShellProcessId;
        session._process = Process.GetProcessById(transfer.ShellProcessId);
        session.StartReader();
        session._process.EnableRaisingEvents = true;
        session._process.Exited += (_, _) => session.ReportExit();
        if (session._process.HasExited)
        {
            session.ReportExit();
        }

        return session;
    }

    /// <summary>A blocked read can only be ended from outside; bytes of a read that completed are given out first.</summary>
    private void StopReader()
    {
        _stopReading = true;
        var thread = OpenThread(THREAD_TERMINATE, false, _readerThreadId);
        try
        {
            while (_reader is { IsAlive: true } reader)
            {
                _ = CancelSynchronousIo(thread);
                if (reader.Join(20))
                {
                    break;
                }
            }
        }
        finally
        {
            if (thread != IntPtr.Zero)
            {
                _ = CloseHandle(thread);
            }
        }
    }

    private void ReportExit()
    {
        if (_handedOff)
        {
            return;
        }

        if (Interlocked.Exchange(ref _exitReported, 1) == 0 && _process is { } process)
        {
            _onExited(SafeExitCode(process));
        }
    }

    private PROCESS_INFORMATION CreateAttachedProcess(ConPtyStartInfo startInfo)
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
        _readerThreadId = GetCurrentThreadId();
        var bytes = new byte[16 * 1024];
        try
        {
            // Bytes of a character cut by the read wait in _pending, so that a move to another process can take them along.
            while (!_stopReading && _output is { } output)
            {
                var carried = _pending.Length;
                _pending.CopyTo(bytes, 0);
                var read = output.Read(bytes, carried, bytes.Length - carried);
                if (read == 0)
                {
                    break;
                }

                var total = carried + read;
                var complete = total - Utf8.IncompleteTail(bytes.AsSpan(0, total));
                _pending = bytes[complete..total];
                if (complete > 0)
                {
                    _onOutput(Encoding.UTF8.GetString(bytes, 0, complete));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // StopReader: nothing was read by this call.
        }
        catch (IOException)
        {
            // Pipe closed: the pseudo console is gone; or StopReader.
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

/// <summary>UTF-8 cut at a read's end (issue #122).</summary>
public static class Utf8
{
    /// <summary>How many bytes at the end begin a character that is not complete yet.</summary>
    public static int IncompleteTail(ReadOnlySpan<byte> bytes)
    {
        for (var back = 1; back <= 3 && back <= bytes.Length; back++)
        {
            var value = bytes[^back];
            if ((value & 0xC0) == 0x80)
            {
                continue; // a continuation byte: look further back
            }

            var length = value < 0x80 ? 1 : (value & 0xE0) == 0xC0 ? 2 : (value & 0xF0) == 0xE0 ? 3 : (value & 0xF8) == 0xF0 ? 4 : 1;
            return length > back ? back : 0;
        }

        return 0;
    }
}
