using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Habilis.Gpu;

/// <summary>CPU load and RAM of the whole computer at one moment.</summary>
internal sealed record SystemSample(double? CpuPercent, double? RamUsedMiB, double? RamTotalMiB)
{
    public double? RamPercent => RamUsedMiB is { } used && RamTotalMiB is > 0 ? used * 100 / RamTotalMiB : null;
}

/// <summary>A process using a GPU, as nvtop lists it: GPU load and memory, CPU load and RAM.</summary>
internal sealed record ProcessUse(int Pid, string Name, string Kind, double? GpuPercent, double? GpuMemoryMiB, double? CpuPercent, double? RamMiB);

/// <summary>Everything the nvtop view shows at one moment.</summary>
internal sealed record LocalSample(GpuSnapshot Gpus, SystemSample System, IReadOnlyList<ProcessUse> Processes);

/// <summary>The computer's CPU, RAM and per-process GPU use; a fake stands in for it in the tests.</summary>
internal interface ISystemSource
{
    /// <summary>A sample; values that need two readings (loads) are null the first time.</summary>
    (SystemSample System, IReadOnlyList<ProcessUse> Processes) Sample(IReadOnlyList<GpuProcess> nvidiaProcesses);
}

/// <summary>
/// Windows' own numbers (issue #79): GetSystemTimes and GlobalMemoryStatusEx for CPU and RAM, the "GPU Engine" and
/// "GPU Process Memory" performance counters for each process's GPU load and dedicated memory (they work for every
/// GPU, also where nvidia-smi reports N/A on WDDM), and process times for each process's CPU load.
/// </summary>
internal sealed partial class WindowsSystem : ISystemSource
{
    private readonly Dictionary<string, CounterSample> _engines = [];
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _processCpu = [];
    private (long Idle, long Total)? _cpu;

    public (SystemSample System, IReadOnlyList<ProcessUse> Processes) Sample(IReadOnlyList<GpuProcess> nvidiaProcesses)
    {
        var system = new SystemSample(CpuPercent(), RamUsed(out var total), total);
        var gpuLoad = GpuLoadByProcess();
        var gpuMemory = GpuMemoryByProcess();
        var compute = nvidiaProcesses.Select(p => p.Pid).ToHashSet();
        var pids = compute.Concat(gpuLoad.Where(l => l.Value >= 0.5).Select(l => l.Key)).Concat(gpuMemory.Where(m => m.Value >= 1).Select(m => m.Key)).Distinct();
        var now = DateTime.UtcNow;
        var processes = new List<ProcessUse>();
        foreach (var pid in pids)
        {
            if (Describe(pid, now) is not { } process)
            {
                continue;
            }

            var nvidia = nvidiaProcesses.FirstOrDefault(p => p.Pid == pid);
            processes.Add(new ProcessUse(
                pid,
                process.Name,
                compute.Contains(pid) ? "Compute" : "Graphic",
                gpuLoad.TryGetValue(pid, out var load) ? load : null,
                gpuMemory.TryGetValue(pid, out var memory) ? memory : nvidia?.MemoryMiB,
                process.Cpu,
                process.Ram));
        }

        foreach (var gone in _processCpu.Keys.Where(k => processes.All(p => p.Pid != k)).ToList())
        {
            _processCpu.Remove(gone);
        }

        return (system, processes.OrderByDescending(p => p.GpuPercent ?? 0).ThenByDescending(p => p.GpuMemoryMiB ?? 0).ToList());
    }

    private double? CpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        var total = kernel + user; // kernel time includes idle time
        var previous = _cpu;
        _cpu = (idle, total);
        return previous is { } p && total > p.Total ? Math.Clamp((1 - ((double)(idle - p.Idle) / (total - p.Total))) * 100, 0, 100) : null;
    }

    private static double? RamUsed(out double? totalMiB)
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            totalMiB = null;
            return null;
        }

        totalMiB = status.TotalPhys / 1048576.0;
        return (status.TotalPhys - status.AvailPhys) / 1048576.0;
    }

    /// <summary>Per process: the busiest engine type's load (as Task Manager shows it), from two counter readings.</summary>
    private Dictionary<int, double> GpuLoadByProcess()
    {
        var result = new Dictionary<int, double>();
        InstanceDataCollection data;
        try
        {
            data = new PerformanceCounterCategory("GPU Engine").ReadCategory()["Utilization Percentage"];
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return result;
        }

        if (data is null)
        {
            return result;
        }

        var byType = new Dictionary<(int Pid, string Type), double>();
        var seen = new HashSet<string>();
        foreach (InstanceData instance in data.Values)
        {
            var name = instance.InstanceName;
            seen.Add(name);
            var sample = instance.Sample;
            if (_engines.TryGetValue(name, out var previous) && EngineName().Match(name) is { Success: true } match)
            {
                var value = CounterSample.Calculate(previous, sample);
                var key = (int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), match.Groups[2].Value);
                byType[key] = byType.GetValueOrDefault(key) + value;
            }

            _engines[name] = sample;
        }

        foreach (var stale in _engines.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _engines.Remove(stale);
        }

        foreach (var ((pid, _), value) in byType)
        {
            result[pid] = Math.Min(100, Math.Max(result.GetValueOrDefault(pid), value));
        }

        return result;
    }

    private static Dictionary<int, double> GpuMemoryByProcess()
    {
        var result = new Dictionary<int, double>();
        try
        {
            var data = new PerformanceCounterCategory("GPU Process Memory").ReadCategory()["Dedicated Usage"];
            if (data is null)
            {
                return result;
            }

            foreach (InstanceData instance in data.Values)
            {
                if (ProcessName().Match(instance.InstanceName) is { Success: true } match)
                {
                    var pid = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    result[pid] = result.GetValueOrDefault(pid) + (instance.RawValue / 1048576.0);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // No such counters on this Windows: no per-process memory.
        }

        return result;
    }

    private (string Name, double? Cpu, double? Ram)? Describe(int pid, DateTime now)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            double? cpu = null;
            try
            {
                var used = process.TotalProcessorTime;
                if (_processCpu.TryGetValue(pid, out var previous) && now > previous.At)
                {
                    cpu = Math.Max(0, (used - previous.Cpu).TotalMilliseconds / (now - previous.At).TotalMilliseconds / Environment.ProcessorCount * 100);
                }

                _processCpu[pid] = (used, now);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Another user's or a protected process: no CPU time.
            }

            return (process.ProcessName, cpu, process.WorkingSet64 / 1048576.0);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null; // the process ended
        }
    }

    [GeneratedRegex(@"pid_(\d+)_.*engtype_(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex EngineName();

    [GeneratedRegex(@"pid_(\d+)_", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessName();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
