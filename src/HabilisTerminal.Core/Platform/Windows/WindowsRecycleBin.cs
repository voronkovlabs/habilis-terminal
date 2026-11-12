using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HabilisTerminal.Core.Interop;

namespace HabilisTerminal.Core.Platform.Windows;

/// <summary>
/// Moving items to the Recycle Bin through the shell, without shell UI. The shell would silently delete permanently
/// when recycling is impossible, so <see cref="CannotRecycleReason"/> is checked first and the user is asked (SPEC §8).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsRecycleBin : IRecycleBin
{
    /// <summary>Why the item cannot go to the Recycle Bin, or null when it can.</summary>
    public string? CannotRecycleReason(string path)
    {
        var full = Path.GetFullPath(path);
        if (FileSystem.LongPath.IsUnc(full))
        {
            return "network locations have no Recycle Bin";
        }

        if (full.Length > RecycleBin.MaxShellPath)
        {
            return "the path is too long for the Recycle Bin";
        }

        var root = Path.GetPathRoot(full);
        try
        {
            var drive = new DriveInfo(root!);
            if (drive.DriveType is DriveType.Network)
            {
                return "network drives have no Recycle Bin";
            }

            if (drive.DriveType is DriveType.Removable or DriveType.CDRom or DriveType.Ram)
            {
                return $"{drive.DriveType.ToString().ToLowerInvariant()} drives have no Recycle Bin";
            }
        }
        catch (ArgumentException)
        {
            return "the drive is unknown";
        }

        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(root!, ref info) == 0 ? null : "the drive has no Recycle Bin";
    }

    /// <summary>Moves one item to the Recycle Bin. Call only after <see cref="CannotRecycleReason"/> returned null.</summary>
    /// <exception cref="IOException">The shell reported an error.</exception>
    public void Recycle(string path)
    {
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0",

            // FOF_WANTNUKEWARNING: should the shell still decide to delete permanently, it asks instead of doing it silently.
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_WANTNUKEWARNING,
        };

        var result = SHFileOperation(ref operation);
        if (result != 0)
        {
            throw new IOException($"The shell could not move '{path}' to the Recycle Bin (code 0x{result:X}).");
        }

        if (operation.fAnyOperationsAborted)
        {
            throw new OperationCanceledException("Moving to the Recycle Bin was aborted.");
        }
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4;
    private const ushort FOF_NOCONFIRMATION = 0x10;
    private const ushort FOF_ALLOWUNDO = 0x40;
    private const ushort FOF_NOERRORUI = 0x400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;

        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);
}
