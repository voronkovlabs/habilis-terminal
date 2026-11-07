using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using HabilisTerminal.Core.FileSystem;
using Microsoft.Win32.SafeHandles;

namespace HabilisTerminal.Core.Interop;

/// <summary>Win32 file APIs that .NET does not expose (all paths go through <see cref="LongPath.ToExtended"/>).</summary>
internal static class NativeFile
{
    public const int ErrorFileExists = 80;
    public const int ErrorAlreadyExists = 183;
    public const int ErrorSharingViolation = 32;
    public const int ErrorLockViolation = 33;
    public const int ErrorPrivilegeNotHeld = 1314;
    public const int ErrorRequestAborted = 1235;
    public const int ErrorNotSameDevice = 17;

    private const uint CopyFileFailIfExists = 0x1;
    private const uint CopyFileCopySymlink = 0x800;
    private const uint CopyFileNoBuffering = 0x1000;
    private const uint CopyFileAllowDecryptedDestination = 0x8;
    private const int ProgressContinue = 0;
    private const int ProgressCancel = 1;

    /// <summary>Files from this size are copied without the system cache (SPEC §8).</summary>
    public const long UnbufferedThreshold = 256L * 1024 * 1024;

    private delegate int CopyProgressRoutine(long totalFileSize, long totalBytesTransferred, long streamSize, long streamBytesTransferred,
        uint streamNumber, uint callbackReason, IntPtr sourceFile, IntPtr destinationFile, IntPtr data);

    /// <summary>
    /// Copies one file with ADS, attributes and times. <paramref name="progress"/> gets bytes copied so far and may
    /// block (pause) or return false (cancel: the partial target is deleted by the system).
    /// </summary>
    /// <exception cref="Win32Exception">The copy failed; <see cref="Win32Exception.NativeErrorCode"/> tells why.</exception>
    public static void CopyFile(string source, string target, bool copyLinkItself, bool failIfExists, Func<long, long, bool> progress)
    {
        var flags = CopyFileAllowDecryptedDestination;
        if (copyLinkItself)
        {
            flags |= CopyFileCopySymlink;
        }

        if (failIfExists)
        {
            flags |= CopyFileFailIfExists;
        }

        try
        {
            if (new FileInfo(source).Length >= UnbufferedThreshold)
            {
                flags |= CopyFileNoBuffering;
            }
        }
        catch (IOException)
        {
        }

        CopyProgressRoutine routine = (total, transferred, _, _, _, _, _, _, _) => progress(transferred, total) ? ProgressContinue : ProgressCancel;
        var cancel = 0;
        if (!CopyFileEx(LongPath.ToExtended(source), LongPath.ToExtended(target), routine, IntPtr.Zero, ref cancel, flags))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        GC.KeepAlive(routine);
    }

    /// <summary>Renames / moves within one volume (atomic, instant). Case-only renames work.</summary>
    public static void Move(string source, string target)
    {
        if (!MoveFileEx(LongPath.ToExtended(source), LongPath.ToExtended(target), 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>The mount point of the volume holding <paramref name="path"/> (e.g. <c>C:\</c> or a mounted folder).</summary>
    public static string VolumeOf(string path)
    {
        var buffer = new char[1024];
        var full = Path.GetFullPath(path);
        return GetVolumePathName(LongPath.ToExtended(full), buffer, buffer.Length)
            ? LongPath.ToDisplay(new string(buffer, 0, Array.IndexOf(buffer, '\0') is var end and >= 0 ? end : buffer.Length))
            : Path.GetPathRoot(full) ?? full;
    }

    public static bool SameVolume(string a, string b) =>
        string.Equals(VolumeOf(a), VolumeOf(b), StringComparison.OrdinalIgnoreCase);

    public static long? FreeBytes(string directory) =>
        GetDiskFreeSpaceEx(LongPath.ToExtended(directory), out var available, out _, out _) ? (long)available : null;

    /// <summary>Creates a junction (directory mount point) at <paramref name="junction"/> pointing to <paramref name="target"/>.</summary>
    public static void CreateJunction(string junction, string target)
    {
        Directory.CreateDirectory(junction);
        var targetFull = Path.GetFullPath(target);
        var substitute = @"\??\" + (targetFull.EndsWith('\\') ? targetFull : targetFull + '\\');
        var print = targetFull;
        var substituteBytes = Encoding.Unicode.GetBytes(substitute);
        var printBytes = Encoding.Unicode.GetBytes(print);

        // REPARSE_DATA_BUFFER for IO_REPARSE_TAG_MOUNT_POINT.
        var pathBufferLength = substituteBytes.Length + 2 + printBytes.Length + 2;
        var dataLength = 8 + pathBufferLength;
        var buffer = new byte[8 + dataLength];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);           // ReparseTag
        BitConverter.GetBytes((ushort)dataLength).CopyTo(buffer, 4);     // ReparseDataLength
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);              // SubstituteNameOffset
        BitConverter.GetBytes((ushort)substituteBytes.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substituteBytes.Length + 2)).CopyTo(buffer, 12); // PrintNameOffset
        BitConverter.GetBytes((ushort)printBytes.Length).CopyTo(buffer, 14);
        substituteBytes.CopyTo(buffer, 16);
        printBytes.CopyTo(buffer, 16 + substituteBytes.Length + 2);

        using var handle = OpenReparsePoint(junction, write: true);
        if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, null, 0, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            Directory.Delete(junction);
            throw new Win32Exception(error);
        }
    }

    public const uint ReparseTagMountPoint = 0xA0000003;
    public const uint ReparseTagSymlink = 0xA000000C;

    /// <summary>The reparse tag of a link (junction, symlink, …), or 0 for ordinary items.</summary>
    public static uint ReparseTag(string path)
    {
        var handle = FindFirstFile(LongPath.ToExtended(path.TrimEnd('\\')), out var data);
        if (handle == new IntPtr(-1))
        {
            return 0;
        }

        FindClose(handle);
        return (data.dwFileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ? data.dwReserved0 : 0;
    }

    /// <summary>The raw reparse data of a path, or null. Opening the link itself needs no right to list it.</summary>
    public static byte[]? ReadReparseData(string path)
    {
        using var handle = CreateFile(LongPath.ToExtended(path.TrimEnd('\\')), 0, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new byte[MaxReparseData];
        return DeviceIoControl(handle, FsctlGetReparsePoint, null, 0, buffer, buffer.Length, out var returned, IntPtr.Zero) ? buffer[..returned] : null;
    }

    /// <summary>How many names the file has; 1 when it cannot be told.</summary>
    public static int HardLinkCount(string path)
    {
        using var handle = CreateFile(LongPath.ToExtended(path.TrimEnd('\\')), 0, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        return !handle.IsInvalid && GetFileInformationByHandle(handle, out var information) ? (int)information.NumberOfLinks : 1;
    }

    /// <summary>All the file's names, as full paths, up to a limit.</summary>
    public static IReadOnlyList<string> HardLinkNames(string path)
    {
        const int MaxNames = 32;
        var names = new List<string>();
        var root = Path.GetPathRoot(Path.GetFullPath(path))?.TrimEnd('\\') ?? string.Empty;
        var buffer = new char[32768];
        var length = buffer.Length;
        var find = FindFirstFileNameW(LongPath.ToExtended(path), 0, ref length, buffer);
        if (find == new IntPtr(-1))
        {
            return names;
        }

        try
        {
            do
            {
                names.Add(root + new string(buffer, 0, Math.Max(0, length - 1)));
                length = buffer.Length;
            }
            while (names.Count < MaxNames && FindNextFileNameW(find, ref length, buffer));
        }
        finally
        {
            FindClose(find);
        }

        return names;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindFirstFileNameW(string fileName, uint flags, ref int length, [Out] char[] linkName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileNameW(IntPtr find, ref int length, [Out] char[] linkName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindFirstFile(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);

    /// <summary>Turns NTFS compression on or off for a file or directory.</summary>
    public static void SetCompression(string path, bool compressed)
    {
        using var handle = CreateFile(LongPath.ToExtended(path), GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var format = BitConverter.GetBytes((ushort)(compressed ? 1 : 0)); // COMPRESSION_FORMAT_DEFAULT / NONE
        if (!DeviceIoControl(handle, FsctlSetCompression, format, format.Length, null, 0, out _, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static SafeFileHandle OpenReparsePoint(string path, bool write)
    {
        var handle = CreateFile(LongPath.ToExtended(path), write ? GenericWrite : GenericRead, 0, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        return handle.IsInvalid ? throw new Win32Exception(Marshal.GetLastWin32Error()) : handle;
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const uint FileShareDelete = 0x4;
    private const int MaxReparseData = 16 * 1024;
    private const uint FsctlSetCompression = 0x0009C040;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(string lpExistingFileName, string lpNewFileName, CopyProgressRoutine lpProgressRoutine, IntPtr lpData, ref int pbCancel, uint dwCopyFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string lpszFileName, [Out] char[] lpszVolumePathName, int cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[]? lpInBuffer, int nInBufferSize,
        byte[]? lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);
}
