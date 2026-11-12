using System.Runtime.InteropServices;

namespace HabilisTerminal.Core.Diagnostics;

/// <summary>
/// How busy the machine is (issue #164). On a machine at full load wall-clock times include waiting for a processor or a
/// disk, so a report needs these to be read. Failing counters, and systems other than Windows, only leave the figures out.
/// </summary>
public sealed class MachineLoad : IDisposable
{
    private const uint Double = 0x00000200;
    private readonly IntPtr _query;
    private readonly IntPtr _queueCounter;
    private readonly IntPtr _latencyCounter;

    public MachineLoad()
    {
        if (!OperatingSystem.IsWindows() || PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            return;
        }

        _ = PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\Current Disk Queue Length", IntPtr.Zero, out _queueCounter);
        _ = PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\Avg. Disk sec/Transfer", IntPtr.Zero, out _latencyCounter);
    }

    /// <summary>Where a span starts: the disk counters' first reading.</summary>
    public void Mark()
    {
        if (_query != IntPtr.Zero)
        {
            _ = PdhCollectQueryData(_query);
        }
    }

    /// <summary>Since <see cref="Mark"/>: the disk queue now and the mean time of a transfer, in milliseconds.</summary>
    public (double? Queue, double? TransferMs) ReadDisk()
    {
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0)
        {
            return (null, null);
        }

        var seconds = Read(_latencyCounter);
        return (Read(_queueCounter), seconds * 1000);
    }

    /// <summary>Total and idle processor time so far, to compare two moments with <see cref="BusyPercent"/>.</summary>
    public static (long Total, long Idle)? ProcessorTimes() =>
        OperatingSystem.IsWindows() && GetSystemTimes(out var idle, out var kernel, out var user) ? (kernel + user, idle) : null;

    /// <summary>Percent of the machine's processors busy between two moments; kernel time includes idle time.</summary>
    public static int? BusyPercent((long Total, long Idle)? from, (long Total, long Idle)? to)
    {
        if (from is not { } start || to is not { } end || end.Total <= start.Total)
        {
            return null;
        }

        var total = end.Total - start.Total;
        return (int)Math.Clamp(Math.Round(100.0 * (total - (end.Idle - start.Idle)) / total), 0, 100);
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            _ = PdhCloseQuery(_query);
        }
    }

    private static double? Read(IntPtr counter)
    {
        if (counter == IntPtr.Zero || PdhGetFormattedCounterValue(counter, Double, out _, out var value) != 0 || value.Status != 0)
        {
            return null;
        }

        return value.Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue
    {
        public uint Status;
        public double Value;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? source, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
