using System.Runtime.InteropServices;

using System.Runtime.Versioning;

namespace HabilisTerminal.Core.Platform.Windows;

/// <summary>Which processes hold a file (Restart Manager), for "file in use" errors (SPEC §8).</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsFileLocks : IFileLocks
{
    private const int ErrorMoreData = 234;
    private const int CchRmSessionKey = 32;

    /// <summary>Comma-separated process names (with PIDs), or null when nothing is found or the query fails.</summary>
    public string? LockingProcesses(string path)
    {
        if (RmStartSession(out var session, 0, new string('\0', CchRmSessionKey + 1)) != 0)
        {
            return null;
        }

        try
        {
            if (RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, null) != 0)
            {
                return null;
            }

            uint needed = 0;
            uint count = 0;
            var result = RmGetList(session, out needed, ref count, null, out _);
            if (result != ErrorMoreData || needed == 0)
            {
                return null;
            }

            var processes = new RM_PROCESS_INFO[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, processes, out _) != 0)
            {
                return null;
            }

            return string.Join(", ", processes.Take((int)count).Select(p => $"{p.strAppName} ({p.Process.dwProcessId})"));
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications,
        IntPtr rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, out uint lpdwRebootReasons);
}
