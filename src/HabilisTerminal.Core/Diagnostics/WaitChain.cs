using System.Runtime.InteropServices;
using System.Text;

namespace HabilisTerminal.Core.Diagnostics;

/// <summary>
/// What a blocked thread waits for, by Windows' wait chain traversal (issue #164): the kind of object and the thread that
/// holds it. It sees critical sections, mutexes, SendMessage, ALPC and COM, but not SRW locks. Object names are left out,
/// as they can hold paths.
/// </summary>
public static class WaitChain
{
    private const int MaxNodes = 16;
    private const int NodeSize = 280;
    private const int ProcessIdOffset = 8;
    private const int ThreadIdOffset = 12;
    private const int ThreadNode = 8;
    private const uint GetAllInfo = 0x1 | 0x4 | 0x8;

    /// <summary>"critical section &lt; thread Worker &lt; ...", or null when it waits for nothing the traversal knows.</summary>
    /// <param name="threadName">The name of a thread of this process; null when it has none.</param>
    public static string? Describe(int threadId, Func<int, string?> threadName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var session = OpenThreadWaitChainSession(0, IntPtr.Zero);
        if (session == IntPtr.Zero)
        {
            return null;
        }

        var nodes = Marshal.AllocHGlobal(MaxNodes * NodeSize);
        try
        {
            var count = MaxNodes;
            if (!GetThreadWaitChain(session, IntPtr.Zero, GetAllInfo, threadId, ref count, nodes, out var isCycle))
            {
                return null;
            }

            var text = new StringBuilder();
            for (var i = 1; i < count; i++)
            {
                var node = nodes + (i * NodeSize);
                var kind = Marshal.ReadInt32(node);
                var part = kind == ThreadNode ? ThreadPart(Marshal.ReadInt32(node, ProcessIdOffset), Marshal.ReadInt32(node, ThreadIdOffset), threadName) : KindName(kind);
                text.Append(text.Length > 0 ? " < " : string.Empty).Append(part);
            }

            if (isCycle)
            {
                text.Append(" (a deadlock)");
            }

            return text.Length > 0 ? text.ToString() : null;
        }
        finally
        {
            Marshal.FreeHGlobal(nodes);
            CloseThreadWaitChainSession(session);
        }
    }

    private static string ThreadPart(int processId, int threadId, Func<int, string?> threadName)
    {
        if (processId == Environment.ProcessId)
        {
            return threadName(threadId) is { Length: > 0 } name ? $"thread \"{name}\"" : "a thread of this app";
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return $"process {process.ProcessName}";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "another process";
        }
    }

    private static string KindName(int kind) => kind switch
    {
        1 => "critical section",
        2 => "SendMessage",
        3 => "mutex",
        4 => "ALPC",
        5 => "COM",
        6 => "thread wait",
        7 => "process wait",
        9 => "COM activation",
        _ => "unknown object",
    };

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr OpenThreadWaitChainSession(uint flags, IntPtr callback);

    [DllImport("advapi32.dll")]
    private static extern void CloseThreadWaitChainSession(IntPtr session);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadWaitChain(IntPtr session, IntPtr context, uint flags, int threadId, ref int nodeCount, IntPtr nodes, [MarshalAs(UnmanagedType.Bool)] out bool isCycle);
}
