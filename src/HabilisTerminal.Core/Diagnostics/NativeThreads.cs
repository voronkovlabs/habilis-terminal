using System.Runtime.InteropServices;
using System.Text;

namespace HabilisTerminal.Core.Diagnostics;

/// <summary>
/// What Windows says a thread is doing, with the raw wait reason (issue #164). <c>ProcessThread.WaitReason</c> folds every
/// reason above 19 into "Unknown", among them <c>WrAlertByThreadId</c>, which modern locks wait in. Only for 64-bit
/// processes, as the layout below is that of 64-bit Windows.
/// </summary>
public static class NativeThreads
{
    private const int SystemProcessInformation = 5;
    private const uint InfoLengthMismatch = 0xC0000004;
    private const int ProcessIdOffset = 0x50;
    private const int ThreadsOffset = 0x100;
    private const int ThreadSize = 0x50;
    private const int ThreadIdOffset = 0x30;
    private const int ThreadStateOffset = 0x44;
    private const int WaitReasonOffset = 0x48;

    private static readonly string[] WaitReasons =
    [
        "Executive", "FreePage", "PageIn", "PoolAllocation", "DelayExecution", "Suspended", "UserRequest",
        "WrExecutive", "WrFreePage", "WrPageIn", "WrPoolAllocation", "WrDelayExecution", "WrSuspended", "WrUserRequest",
        "WrSpare0", "WrQueue", "WrLpcReceive", "WrLpcReply", "WrVirtualMemory", "WrPageOut", "WrRendezvous", "WrKeyedEvent",
        "WrTerminated", "WrProcessInSwap", "WrCpuRateControl", "WrCalloutStack", "WrKernel", "WrResource", "WrPushLock",
        "WrMutex", "WrQuantumEnd", "WrDispatchInt", "WrPreempted", "WrYieldExecution", "WrFastMutex", "WrGuardedMutex",
        "WrRundown", "WrAlertByThreadId", "WrDeferredPreempt",
    ];

    /// <summary>"running", "ready", or "wait: reason"; null when it cannot be told.</summary>
    public static string? StateOf(int processId, int threadId)
    {
        if (!Environment.Is64BitProcess)
        {
            return null;
        }

        var size = 1024 * 1024;
        while (size <= 64 * 1024 * 1024)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out var needed);
                if (status == InfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + (64 * 1024));
                    continue;
                }

                return status == 0 ? Find(buffer, processId, threadId) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    public static string Describe(int state, int waitReason) => state switch
    {
        2 => "running",
        1 or 3 or 7 => "ready",
        5 => "wait: " + (waitReason >= 0 && waitReason < WaitReasons.Length ? WaitReasons[waitReason] : $"reason {waitReason}"),
        6 => "transition",
        8 => "swapped out",
        _ => $"state {state}",
    };

    /// <summary>The states seen in a stall, most seen first: "wait: Executive 70%, ready 30%".</summary>
    public static string? Summarize(IReadOnlyDictionary<string, int> seen)
    {
        var total = seen.Values.Sum();
        if (total == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var (state, count) in seen.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(3))
        {
            text.Append(text.Length > 0 ? ", " : string.Empty).Append(state).Append(' ').Append((count * 100) / total).Append('%');
        }

        return text.ToString();
    }

    private static string? Find(IntPtr buffer, int processId, int threadId)
    {
        var process = buffer;
        while (true)
        {
            if (Marshal.ReadInt64(process, ProcessIdOffset) == processId)
            {
                var threads = Marshal.ReadInt32(process, 4);
                for (var i = 0; i < threads; i++)
                {
                    var thread = process + ThreadsOffset + (i * ThreadSize);
                    if (Marshal.ReadInt64(thread, ThreadIdOffset) == threadId)
                    {
                        return Describe(Marshal.ReadInt32(thread, ThreadStateOffset), Marshal.ReadInt32(thread, WaitReasonOffset));
                    }
                }

                return null;
            }

            var next = Marshal.ReadInt32(process, 0);
            if (next == 0)
            {
                return null;
            }

            process += next;
        }
    }

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int informationClass, IntPtr information, int length, out int returned);
}
