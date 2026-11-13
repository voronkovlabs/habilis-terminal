using System.Runtime.InteropServices;
using System.Text;

namespace HabilisTerminal.Shell.Pty;

/// <summary>
/// A child process on a pseudo terminal on macOS and Linux (issue #268): <c>forkpty</c>, then <c>execve</c> in the child.
/// After the fork only the forking thread exists in the child, so everything it needs (the program, argv, envp, the
/// folder) is laid out in native memory first and the P/Invoke stubs are bound before the fork: the child runs a few
/// system calls and no managed allocation until <c>execve</c> replaces it.
/// </summary>
public sealed class UnixPseudoTerminal : IPseudoTerminal
{
    private const int SigHup = 1;
    private const int SigDfl = 0;
    private const int LastCatchableSignal = 31;

    private const string LibC = "libc";

    private readonly object _writeLock = new();
    private readonly int _master;
    private readonly Thread _reader;
    private int _disposed;

    // forkpty is in libSystem on macOS and in libc from glibc 2.34.
    static UnixPseudoTerminal()
    {
        if (!OperatingSystem.IsWindows())
        {
            NativeLibrary.SetDllImportResolver(typeof(UnixPseudoTerminal).Assembly, Resolve);
        }
    }

    private UnixPseudoTerminal(int master, int processId, Action<string> onOutput, Action<int> onExited)
    {
        _master = master;
        ProcessId = processId;
        _reader = new Thread(() => Read(onOutput)) { IsBackground = true, Name = "Pty output" };
        _reader.Start();
        new Thread(() => Wait(onExited)) { IsBackground = true, Name = "Pty wait" }.Start();
    }

    public int ProcessId { get; }

    public static unsafe UnixPseudoTerminal Start(PtyStartInfo startInfo, Action<string> onOutput, Action<int> onExited)
    {
        var arguments = new List<string> { startInfo.ArgumentZero ?? startInfo.FileName };
        arguments.AddRange(startInfo.ArgumentList ?? []);
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? string.Empty, StringComparer.Ordinal);
        foreach (var (name, value) in startInfo.Environment ?? new Dictionary<string, string>())
        {
            environment[name] = value;
        }

        var program = NativeString(startInfo.FileName);
        var folder = startInfo.WorkingDirectory is { Length: > 0 } wd ? NativeString(wd) : IntPtr.Zero;
        var argv = NativeArray(arguments);
        var envp = NativeArray([.. environment.Select(p => p.Key + "=" + p.Value)]);
        try
        {
            // Bound now: binding a stub in the child would run the loader and allocate.
            Marshal.Prelink(typeof(UnixPseudoTerminal).GetMethod(nameof(ExecVe), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
            foreach (var name in new[] { nameof(ExitNow), nameof(ChangeDirectory), nameof(SetSignal), nameof(ForkPty) })
            {
                Marshal.Prelink(typeof(UnixPseudoTerminal).GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
            }

            var size = new WinSize { Rows = (ushort)Math.Max(1, startInfo.Rows), Columns = (ushort)Math.Max(1, startInfo.Columns) };
            int master;
            var pid = ForkPty(&master, IntPtr.Zero, IntPtr.Zero, &size);
            if (pid == 0)
            {
                // The child: signals the runtime ignores (SIGPIPE) would stay ignored in the shell; give all back.
                for (var signal = 1; signal <= LastCatchableSignal; signal++)
                {
                    SetSignal(signal, SigDfl);
                }

                if (folder != IntPtr.Zero)
                {
                    _ = ChangeDirectory(folder);
                }

                _ = ExecVe(program, argv, envp);
                ExitNow(127); // execve returns only on failure
            }

            if (pid < 0)
            {
                throw new InvalidOperationException($"forkpty failed (errno {Marshal.GetLastPInvokeError()}).");
            }

            return new UnixPseudoTerminal(master, pid, onOutput, onExited);
        }
        finally
        {
            Marshal.FreeHGlobal(program);
            if (folder != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(folder);
            }

            FreeArray(argv, arguments.Count);
            FreeArray(envp, environment.Count);
        }
    }

    public void Write(string text)
    {
        if (Volatile.Read(ref _disposed) != 0 || text.Length == 0)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_writeLock)
        {
            unsafe
            {
                fixed (byte* start = bytes)
                {
                    var done = 0;
                    while (done < bytes.Length)
                    {
                        var written = WriteFile(_master, start + done, bytes.Length - done);
                        if (written <= 0)
                        {
                            return; // the child is gone
                        }

                        done += (int)written;
                    }
                }
            }
        }
    }

    public unsafe void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var size = new WinSize { Rows = (ushort)Math.Max(1, rows), Columns = (ushort)Math.Max(1, columns) };
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            // ioctl is variadic: on Apple arm64 the variadic argument goes on the stack, after eight register slots.
            _ = IoctlArm64Apple(_master, TiocSetWinSizeMac, 0, 0, 0, 0, 0, 0, &size);
        }
        else
        {
            _ = Ioctl(_master, OperatingSystem.IsMacOS() ? TiocSetWinSizeMac : TiocSetWinSizeLinux, &size);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ = Kill(ProcessId, SigHup);
        _ = Close(_master);
    }

    private unsafe void Read(Action<string> onOutput)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        while (true)
        {
            nint read;
            fixed (byte* buffer = bytes)
            {
                read = ReadFile(_master, buffer, bytes.Length);
            }

            if (read <= 0)
            {
                if (read < 0 && Marshal.GetLastPInvokeError() == EIntr)
                {
                    continue;
                }

                return; // EIO once the child and its children closed the terminal
            }

            var count = decoder.GetChars(bytes, 0, (int)read, chars, 0, flush: false);
            if (count > 0)
            {
                onOutput(new string(chars, 0, count));
            }
        }
    }

    private void Wait(Action<int> onExited)
    {
        int status;
        while (WaitPid(ProcessId, out status, 0) < 0 && Marshal.GetLastPInvokeError() == EIntr)
        {
        }

        _reader.Join(TimeSpan.FromSeconds(2)); // the last output before the exit
        var signal = status & 0x7f;
        onExited(signal == 0 ? (status >> 8) & 0xff : 128 + signal);
    }

    private static IntPtr NativeString(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + '\0');
        var memory = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, memory, bytes.Length);
        return memory;
    }

    private static IntPtr NativeArray(List<string> items)
    {
        var array = Marshal.AllocHGlobal(IntPtr.Size * (items.Count + 1));
        for (var i = 0; i < items.Count; i++)
        {
            Marshal.WriteIntPtr(array, i * IntPtr.Size, NativeString(items[i]));
        }

        Marshal.WriteIntPtr(array, items.Count * IntPtr.Size, IntPtr.Zero);
        return array;
    }

    private static void FreeArray(IntPtr array, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Marshal.FreeHGlobal(Marshal.ReadIntPtr(array, i * IntPtr.Size));
        }

        Marshal.FreeHGlobal(array);
    }

    private const int EIntr = 4;
    private const nuint TiocSetWinSizeMac = 0x80087467;
    private const nuint TiocSetWinSizeLinux = 0x5414;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort PixelWidth;
        public ushort PixelHeight;
    }

    [DllImport(LibC, EntryPoint = "forkpty", SetLastError = true)]
    private static extern unsafe int ForkPty(int* master, IntPtr name, IntPtr termios, WinSize* size);

    [DllImport(LibC, EntryPoint = "execve")]
    private static extern int ExecVe(IntPtr path, IntPtr argv, IntPtr envp);

    [DllImport(LibC, EntryPoint = "_exit")]
    private static extern void ExitNow(int status);

    [DllImport(LibC, EntryPoint = "chdir")]
    private static extern int ChangeDirectory(IntPtr path);

    [DllImport(LibC, EntryPoint = "signal")]
    private static extern IntPtr SetSignal(int signal, nint handler);

    [DllImport(LibC, EntryPoint = "read", SetLastError = true)]
    private static extern unsafe nint ReadFile(int fd, byte* buffer, nint count);

    [DllImport(LibC, EntryPoint = "write", SetLastError = true)]
    private static extern unsafe nint WriteFile(int fd, byte* buffer, nint count);

    [DllImport(LibC, EntryPoint = "ioctl", SetLastError = true)]
    private static extern unsafe int Ioctl(int fd, nuint request, WinSize* size);

    [DllImport(LibC, EntryPoint = "ioctl", SetLastError = true)]
    private static extern unsafe int IoctlArm64Apple(int fd, nuint request, nint r2, nint r3, nint r4, nint r5, nint r6, nint r7, WinSize* size);

    [DllImport(LibC, EntryPoint = "waitpid", SetLastError = true)]
    private static extern int WaitPid(int pid, out int status, int options);

    [DllImport(LibC, EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    [DllImport(LibC, EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    private static IntPtr Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? path) =>
        name == LibC && NativeLibrary.TryLoad(OperatingSystem.IsMacOS() ? "/usr/lib/libSystem.B.dylib" : "libc.so.6", out var handle)
            ? handle
            : IntPtr.Zero;
}
