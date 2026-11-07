using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using HabilisTerminal.Plugins;

namespace Habilis.TaskManager;

internal sealed record ProcessData(
    int Pid,
    string Name,
    string Status,
    string? User,
    double? CpuPercent,
    TimeSpan? CpuTime,
    long? WorkingSet,
    long? PrivateBytes,
    int? Threads,
    int? Handles,
    int? SessionId,
    string? Architecture,
    DateTime? Started,
    string? ExecutablePath,
    string? CommandLine,
    string? Services);

internal sealed record ProcessSnapshot(DateTime TakenUtc, IReadOnlyList<ProcessData> Processes);

internal interface IProcessSource
{
    ProcessSnapshot Sample();

    bool End(ProcessData expected);
}

/// <summary>Windows process data available without elevation; protected-process fields are left blank.</summary>
internal sealed partial class WindowsProcessSource : IProcessSource
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint ScManagerEnumerateService = 0x0004;
    private const int ScEnumProcessInfo = 0;
    private const uint ServiceWin32 = 0x00000030;
    private const uint ServiceActive = 0x00000001;
    private const int ErrorMoreData = 234;
    private const int ProcessCommandLineInformation = 60;
    private static readonly TimeSpan ServiceRefreshInterval = TimeSpan.FromSeconds(5);
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime At, DateTime? Started)> _cpu = [];
    private readonly Dictionary<int, (DateTime? Started, string? User, string? Architecture, string? ExecutablePath, string? CommandLine)> _identity = [];
    private IReadOnlyDictionary<int, string> _services = new Dictionary<int, string>();
    private DateTime _servicesAt;

    public ProcessSnapshot Sample()
    {
        var now = DateTime.UtcNow;
        var services = ServicesByProcess(now);
        var result = new List<ProcessData>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    result.Add(Describe(process, now, services.GetValueOrDefault(process.Id)));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or Win32Exception)
                {
                    // The process ended, or Windows protects it. It can appear on the next sample if still present.
                }
            }
        }

        var present = result.Select(p => p.Pid).ToHashSet();
        foreach (var gone in _cpu.Keys.Where(pid => !present.Contains(pid)).ToList())
        {
            _cpu.Remove(gone);
            _identity.Remove(gone);
        }

        return new ProcessSnapshot(now, result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Pid).ToList());
    }

    public bool End(ProcessData expected)
    {
        try
        {
            using var process = Process.GetProcessById(expected.Pid);
            if (expected.Started is not { } sampled || sampled == default)
            {
                throw new PluginException($"Windows did not expose the start time of {expected.Name} (PID {expected.Pid}), so it cannot be ended safely.");
            }

            var current = Read(() => process.StartTime.ToUniversalTime());
            if (current == default)
            {
                throw new PluginException($"Windows could not verify that {expected.Name} (PID {expected.Pid}) is still the same process.");
            }

            if (sampled != current)
            {
                throw new PluginException($"{expected.Name} (PID {expected.Pid}) has already ended and its PID now belongs to another process.");
            }

            process.Kill(entireProcessTree: false);
            return true;
        }
        catch (ArgumentException)
        {
            // It ended between the last refresh and F8; the requested outcome is already true.
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            throw new PluginException($"Windows could not end {expected.Name} (PID {expected.Pid}): {ex.Message}", ex);
        }
    }

    private ProcessData Describe(Process process, DateTime now, string? services)
    {
        var pid = process.Id;
        var name = Read(() => process.ProcessName) ?? $"PID {pid}";
        var started = Read(() => process.StartTime.ToUniversalTime());
        var cpuTime = Read(() => process.TotalProcessorTime);
        double? cpu = null;
        if (cpuTime is { } used)
        {
            if (_cpu.TryGetValue(pid, out var previous) && previous.Started == started && now > previous.At)
            {
                cpu = Math.Clamp((used - previous.Cpu).TotalMilliseconds / (now - previous.At).TotalMilliseconds / Environment.ProcessorCount * 100, 0, 100);
            }

            _cpu[pid] = (used, now, started);
        }

        if (!_identity.TryGetValue(pid, out var identity) || identity.Started != started)
        {
            var handle = OpenProcess(QueryLimitedInformation, false, pid);
            try
            {
                identity = (started, UserOf(handle), ArchitectureOf(handle), ImagePathOf(handle), CommandLineOf(handle));
                _identity[pid] = identity;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    _ = CloseHandle(handle);
                }
            }
        }

        return new ProcessData(
            pid,
            name,
            Status(process),
            identity.User,
            cpu,
            cpuTime,
            Read(() => process.WorkingSet64),
            Read(() => process.PrivateMemorySize64),
            Read(() => process.Threads.Count),
            Read(() => process.HandleCount),
            Read(() => process.SessionId),
            identity.Architecture,
            started,
            identity.ExecutablePath,
            identity.CommandLine,
            services);
    }

    private IReadOnlyDictionary<int, string> ServicesByProcess(DateTime now)
    {
        if (now - _servicesAt < ServiceRefreshInterval)
        {
            return _services;
        }

        _services = EnumerateActiveServices();
        _servicesAt = now;
        return _services;
    }

    private static unsafe Dictionary<int, string> EnumerateActiveServices()
    {
        var manager = OpenSCManager(IntPtr.Zero, IntPtr.Zero, ScManagerEnumerateService);
        if (manager == IntPtr.Zero)
        {
            return new Dictionary<int, string>();
        }

        try
        {
            uint needed;
            uint returned;
            uint resume = 0;
            _ = EnumServicesStatusEx(manager, ScEnumProcessInfo, ServiceWin32, ServiceActive, null, 0,
                out needed, out returned, ref resume, IntPtr.Zero);
            if (needed == 0 || needed > 16 * 1024 * 1024)
            {
                return new Dictionary<int, string>();
            }

            resume = 0;
            var names = new Dictionary<int, List<string>>();
            while (needed > 0 && needed <= 16 * 1024 * 1024)
            {
                var buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    var size = needed;
                    var complete = EnumServicesStatusEx(manager, ScEnumProcessInfo, ServiceWin32, ServiceActive,
                        (byte*)buffer, size, out needed, out returned, ref resume, IntPtr.Zero);
                    var error = complete ? 0 : Marshal.GetLastPInvokeError();
                    var nativeSize = Marshal.SizeOf<EnumServiceStatusProcess>();
                    for (var index = 0; index < returned; index++)
                    {
                        var service = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + (index * nativeSize));
                        var pid = unchecked((int)service.Status.ProcessId);
                        var name = Marshal.PtrToStringUni(service.ServiceName);
                        if (pid <= 0 || string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        if (!names.TryGetValue(pid, out var processServices))
                        {
                            processServices = [];
                            names[pid] = processServices;
                        }

                        processServices.Add(name);
                    }

                    if (complete || error != ErrorMoreData)
                    {
                        break;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return names.ToDictionary(
                pair => pair.Key,
                pair => string.Join(", ", pair.Value.Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)));
        }
        finally
        {
            _ = CloseServiceHandle(manager);
        }
    }

    private static unsafe string? ImagePathOf(IntPtr process)
    {
        if (process == IntPtr.Zero)
        {
            return null;
        }

        var length = 32768;
        var path = stackalloc char[length];
        return QueryFullProcessImageName(process, 0, path, ref length) ? new string(path, 0, length) : null;
    }

    private static string Status(Process process)
    {
        try
        {
            var any = false;
            foreach (ProcessThread thread in process.Threads)
            {
                using (thread)
                {
                    any = true;
                    if (thread.ThreadState != System.Diagnostics.ThreadState.Wait || thread.WaitReason != ThreadWaitReason.Suspended)
                    {
                        return "Running";
                    }
                }
            }

            return any ? "Suspended" : "Running";
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            return "Running";
        }
    }

    private static string? UserOf(IntPtr process)
    {
        if (process == IntPtr.Zero || !OpenProcessToken(process, TokenQuery, out var token))
        {
            return null;
        }

        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.Name;
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    private static string? ArchitectureOf(IntPtr process)
    {
        if (process == IntPtr.Zero || !IsWow64Process2(process, out var processMachine, out var nativeMachine))
        {
            return null;
        }

        var machine = processMachine == 0 ? nativeMachine : processMachine;
        return machine switch
        {
            0x014c => "x86",
            0x8664 => "x64",
            0xAA64 => "ARM64",
            _ => $"0x{machine:X4}",
        };
    }

    private static string? CommandLineOf(IntPtr process)
    {
        if (process == IntPtr.Zero)
        {
            return null;
        }

        _ = NtQueryInformationProcess(process, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
        if (needed <= 0 || needed > 1 << 20)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, needed, out _) != 0)
            {
                return null;
            }

            var length = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return length == 0 || text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static T? Read<T>(Func<T> value)
    {
        try
        {
            return value();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            return default;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusProcess
    {
        public IntPtr ServiceName;
        public IntPtr DisplayName;
        public ServiceStatusProcess Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true)]
    private static partial IntPtr OpenSCManager(IntPtr machineName, IntPtr databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumServicesStatusEx(IntPtr manager, int infoLevel, uint serviceType,
        uint serviceState, byte* services, uint bufferSize, out uint bytesNeeded, out uint servicesReturned,
        ref uint resumeHandle, IntPtr groupName);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr service);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(IntPtr process, uint flags, char* path, ref int size);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returnLength);
}
