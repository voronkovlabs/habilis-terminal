using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace HabilisTerminal.Core.Diagnostics;

/// <summary>
/// The native stack of another process's thread, as module and function names (issue #164). It runs in a helper process:
/// suspending a thread from inside its own process can deadlock with a garbage collection. Frames of code compiled at run
/// time have no names and may end the walk; what a wait is waiting in sits above them. x64 only.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ThreadStacks : IDisposable
{
    public const int MaxFrames = 32;

    /// <summary>A thread stays suspended at most this long, whatever happens to the walk.</summary>
    private const int MaxSuspendedMs = 200;

    private const uint ProcessAccess = 0x0400 | 0x0010; // PROCESS_QUERY_INFORMATION | PROCESS_VM_READ
    private const uint ThreadAccess = 0x0002 | 0x0008 | 0x0040; // SUSPEND_RESUME | GET_CONTEXT | QUERY_INFORMATION
    private const uint OptionsNoNetwork = 0x00000002 | 0x00000004 | 0x00000200 | 0x00010000 | 0x00200000 | 0x02000000; // UNDNAME, DEFERRED_LOADS, FAIL_CRITICAL_ERRORS, IGNORE_NT_SYMPATH, NO_PROMPTS, DISABLE_SYMSRV_AUTODETECT
    private const uint MachineAmd64 = 0x8664;
    private const int ContextSize = 1232;
    private const int ContextFlagsOffset = 0x30;
    private const int ContextFull = 0x10000B;
    private const int RspOffset = 0x98;
    private const int RbpOffset = 0xA0;
    private const int RipOffset = 0xF8;
    private const int FrameSize = 264;
    private const int FlatMode = 3;
    private const int SymbolHeader = 88;
    private const int SymbolNameMax = 256;
    private const int SymbolMaxNameOffset = 80;
    private const int SymbolNameOffset = 84;
    private const long MaxDisplacement = 64 * 1024;

    private readonly IntPtr _process;
    private readonly IntPtr _functionTableAccess;
    private readonly IntPtr _moduleBase;
    private readonly Dictionary<ulong, string> _moduleNames = [];

    /// <param name="processId">The process to read.</param>
    /// <param name="symbolFolder">Where to look for symbol files: only here, never the network.</param>
    public ThreadStacks(int processId, string? symbolFolder = null)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var process = OpenProcess(ProcessAccess, false, processId);
        if (process == IntPtr.Zero)
        {
            return;
        }

        // Without "invade": listing the process's modules here failed now and then while it was loading one (issue #243).
        // Walk refreshes the module list before every stack anyway.
        _ = SymSetOptions(OptionsNoNetwork);
        if (!SymInitializeW(process, symbolFolder ?? string.Empty, false))
        {
            _ = CloseHandle(process);
            return;
        }

        var help = GetModuleHandleW("dbghelp.dll");
        _functionTableAccess = GetProcAddress(help, "SymFunctionTableAccess64");
        _moduleBase = GetProcAddress(help, "SymGetModuleBase64");
        _process = process;
    }

    /// <summary>"ntdll!NtWaitForSingleObject &lt; KERNELBASE!WaitForSingleObjectEx &lt; ...", innermost first; null when it cannot be read.</summary>
    public string? Walk(int threadId)
    {
        if (_process == IntPtr.Zero)
        {
            return null;
        }

        var thread = OpenThread(ThreadAccess, false, threadId);
        if (thread == IntPtr.Zero)
        {
            return null;
        }

        var raw = Marshal.AllocHGlobal(ContextSize + 16);
        var frame = Marshal.AllocHGlobal(FrameSize);
        var symbol = Marshal.AllocHGlobal(SymbolHeader + (SymbolNameMax * 2));
        try
        {
            // Listing the modules fails now and then while the process loads one (issue #243): a few tries.
            for (var attempt = 0; attempt < 5 && !SymRefreshModuleList(_process); attempt++)
            {
                Thread.Sleep(20);
            }
            var context = new IntPtr((raw.ToInt64() + 15) & ~15L);
            Clear(context, ContextSize);
            Marshal.WriteInt32(context, ContextFlagsOffset, ContextFull);
            if (SuspendThread(thread) == uint.MaxValue)
            {
                return null;
            }

            var resumed = 0;
            void Resume()
            {
                if (Interlocked.Exchange(ref resumed, 1) == 0)
                {
                    _ = ResumeThread(thread);
                }
            }

            using var guard = new Timer(_ => Resume(), null, MaxSuspendedMs, Timeout.Infinite);
            try
            {
                if (!GetThreadContext(thread, context))
                {
                    return null;
                }

                Clear(frame, FrameSize);
                SetAddress(frame, 0, Marshal.ReadInt64(context, RipOffset));
                SetAddress(frame, 32, Marshal.ReadInt64(context, RbpOffset));
                SetAddress(frame, 48, Marshal.ReadInt64(context, RspOffset));
                var text = new StringBuilder();
                var last = string.Empty;
                for (var i = 0; i < MaxFrames && StackWalk64(MachineAmd64, _process, thread, frame, context, IntPtr.Zero, _functionTableAccess, _moduleBase, IntPtr.Zero); i++)
                {
                    var address = (ulong)Marshal.ReadInt64(frame, 0);
                    if (address == 0)
                    {
                        break;
                    }

                    var name = Name(address, symbol);
                    if (name != last)
                    {
                        text.Append(text.Length > 0 ? " < " : string.Empty).Append(name);
                        last = name;
                    }
                }

                return text.Length > 0 ? text.ToString() : null;
            }
            finally
            {
                Resume();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(symbol);
            Marshal.FreeHGlobal(frame);
            Marshal.FreeHGlobal(raw);
            _ = CloseHandle(thread);
        }
    }

    public void Dispose()
    {
        if (_process != IntPtr.Zero)
        {
            _ = SymCleanup(_process);
            _ = CloseHandle(_process);
        }
    }

    /// <summary>The helper process: a thread id on each line in, its stack on a line out, an empty line when unknown. It ends when its input does.</summary>
    public static void RunHelper(int processId, TextReader input, TextWriter output)
    {
        using var stacks = new ThreadStacks(processId, AppContext.BaseDirectory);
        while (input.ReadLine() is { } line)
        {
            var stack = int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var threadId) ? stacks.Walk(threadId) : null;
            output.WriteLine(stack ?? string.Empty);
            output.Flush();
        }
    }

    private string Name(ulong address, IntPtr symbol)
    {
        var moduleBase = SymGetModuleBase64(_process, address);
        if (moduleBase == 0)
        {
            return "?";
        }

        var module = ModuleName(moduleBase);
        Clear(symbol, SymbolHeader);
        Marshal.WriteInt32(symbol, 0, SymbolHeader);
        Marshal.WriteInt32(symbol, SymbolMaxNameOffset, SymbolNameMax);
        if (SymFromAddrW(_process, address, out var displacement, symbol) && displacement < MaxDisplacement &&
            Marshal.PtrToStringUni(symbol + SymbolNameOffset) is { Length: > 0 } function)
        {
            return $"{module}!{function}";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{module}+0x{address - moduleBase:x}");
    }

    /// <summary>The file's name without its folder: paths never leave the machine.</summary>
    private string ModuleName(ulong moduleBase)
    {
        if (_moduleNames.TryGetValue(moduleBase, out var known))
        {
            return known;
        }

        var path = new char[1024];
        var length = GetModuleFileNameExW(_process, new IntPtr(unchecked((long)moduleBase)), path, path.Length);
        var name = length > 0 ? Path.GetFileNameWithoutExtension(new string(path, 0, (int)length)) : "?";
        _moduleNames[moduleBase] = name;
        return name;
    }

    private static void SetAddress(IntPtr frame, int offset, long address)
    {
        Marshal.WriteInt64(frame, offset, address);
        Marshal.WriteInt32(frame, offset + 12, FlatMode);
    }

    private static void Clear(IntPtr memory, int size)
    {
        for (var i = 0; i < size; i += 8)
        {
            Marshal.WriteInt64(memory, i, 0);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int threadId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(IntPtr thread);

    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadContext(IntPtr thread, IntPtr context);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameExW(IntPtr process, IntPtr module, [Out] char[] name, int size);

    [DllImport("dbghelp.dll")]
    private static extern uint SymSetOptions(uint options);

    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SymInitializeW(IntPtr process, string searchPath, [MarshalAs(UnmanagedType.Bool)] bool invade);

    [DllImport("dbghelp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SymCleanup(IntPtr process);

    [DllImport("dbghelp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SymRefreshModuleList(IntPtr process);

    [DllImport("dbghelp.dll")]
    private static extern ulong SymGetModuleBase64(IntPtr process, ulong address);

    [DllImport("dbghelp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SymFromAddrW(IntPtr process, ulong address, out long displacement, IntPtr symbol);

    [DllImport("dbghelp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StackWalk64(uint machine, IntPtr process, IntPtr thread, IntPtr frame, IntPtr context, IntPtr readMemory, IntPtr functionTableAccess, IntPtr getModuleBase, IntPtr translateAddress);
}
