using HabilisTerminal.Core.Platform;

namespace HabilisTerminal.Core.Interop;

/// <summary>
/// File calls .NET does not offer everywhere, through <see cref="Platforms.Current"/> (#259). The error codes are the
/// Windows ones on every system.
/// </summary>
internal static class NativeFile
{
    public const int ErrorFileExists = 80;
    public const int ErrorAlreadyExists = 183;
    public const int ErrorSharingViolation = 32;
    public const int ErrorLockViolation = 33;
    public const int ErrorPrivilegeNotHeld = 1314;
    public const int ErrorRequestAborted = 1235;
    public const int ErrorNotSameDevice = 17;

    /// <summary>Files from this size are copied without the system cache (SPEC §8).</summary>
    public const long UnbufferedThreshold = 256L * 1024 * 1024;

    public const uint ReparseTagMountPoint = 0xA0000003;
    public const uint ReparseTagSymlink = 0xA000000C;

    private static IFileSystemNative Native => Platforms.Current.FileSystem;

    /// <summary>
    /// Copies one file with ADS, attributes and times. <paramref name="progress"/> gets bytes copied so far and may
    /// block (pause) or return false (cancel: the partial target is deleted).
    /// </summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The copy failed; <see cref="System.ComponentModel.Win32Exception.NativeErrorCode"/> tells why.</exception>
    public static void CopyFile(string source, string target, bool copyLinkItself, bool failIfExists, Func<long, long, bool> progress) =>
        Native.CopyFile(source, target, copyLinkItself, failIfExists, progress);

    /// <summary>Renames / moves within one volume (atomic, instant). Case-only renames work.</summary>
    public static void Move(string source, string target) => Native.Move(source, target);

    /// <summary>The mount point of the volume holding <paramref name="path"/> (e.g. <c>C:\</c> or a mounted folder).</summary>
    public static string VolumeOf(string path) => Native.VolumeOf(path);

    public static bool SameVolume(string a, string b) =>
        string.Equals(VolumeOf(a), VolumeOf(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static long? FreeBytes(string directory) => Native.FreeBytes(directory);

    /// <summary>Creates a junction (directory mount point; a symbolic link elsewhere) at <paramref name="junction"/>.</summary>
    public static void CreateJunction(string junction, string target) => Native.CreateJunction(junction, target);

    /// <summary>The reparse tag of a link (junction, symlink, …), or 0 for ordinary items.</summary>
    public static uint ReparseTag(string path) => Native.ReparseTag(path);

    /// <summary>The raw reparse data of a path, or null.</summary>
    public static byte[]? ReadReparseData(string path) => Native.ReadReparseData(path);

    /// <summary>How many names the file has; 1 when it cannot be told.</summary>
    public static int HardLinkCount(string path) => Native.HardLinkCount(path);

    /// <summary>All the file's names, as full paths, up to a limit.</summary>
    public static IReadOnlyList<string> HardLinkNames(string path) => Native.HardLinkNames(path);

    /// <summary>Turns NTFS compression on or off for a file or directory.</summary>
    public static void SetCompression(string path, bool compressed) => Native.SetCompression(path, compressed);
}
