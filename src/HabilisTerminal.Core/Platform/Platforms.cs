using System.Runtime.Versioning;
using HabilisTerminal.Core.Platform.Portable;
using HabilisTerminal.Core.Platform.Windows;

namespace HabilisTerminal.Core.Platform;

/// <summary>
/// File system calls .NET does not offer on every OS (copy with progress, links, volumes, compression). Errors come as
/// <see cref="System.ComponentModel.Win32Exception"/> with the Windows error codes of <see cref="Interop.NativeFile"/>,
/// whatever the OS, so the operations read them one way.
/// </summary>
public interface IFileSystemNative
{
    void CopyFile(string source, string target, bool copyLinkItself, bool failIfExists, Func<long, long, bool> progress);

    void Move(string source, string target);

    string VolumeOf(string path);

    long? FreeBytes(string directory);

    void CreateJunction(string junction, string target);

    /// <summary>The Windows reparse tag of a link, or 0 for ordinary items (and on other systems).</summary>
    uint ReparseTag(string path);

    byte[]? ReadReparseData(string path);

    int HardLinkCount(string path);

    IReadOnlyList<string> HardLinkNames(string path);

    /// <summary>Whether <see cref="SetCompression"/> and <see cref="SetEncryption"/> can work here.</summary>
    bool HasCompressionAndEncryption { get; }

    void SetCompression(string path, bool compressed);

    void SetEncryption(string path, bool encrypted);

    /// <summary>Copies the access control list from one file to another, where the system has one.</summary>
    void CopySecurity(string source, string target);
}

/// <summary>The system's trash (Recycle Bin on Windows).</summary>
public interface IRecycleBin
{
    /// <summary>Why the item cannot go to the trash, or null when it can.</summary>
    string? CannotRecycleReason(string path);

    void Recycle(string path);
}

/// <summary>Which processes hold a file open.</summary>
public interface IFileLocks
{
    /// <summary>A readable list of the processes, or null when unknown.</summary>
    string? LockingProcesses(string path);
}

/// <summary>Well-known folders and network drives for the drive menu.</summary>
public interface IDriveSource
{
    string? DownloadsFolder();

    bool CanDisconnect { get; }

    void DisconnectNetworkDrive(string root);
}

/// <summary>The implementations for the system the app runs on (#259); tests may swap one.</summary>
public sealed record Platforms(IFileSystemNative FileSystem, IRecycleBin RecycleBin, IFileLocks FileLocks, IDriveSource Drives)
{
    public static Platforms Current { get; set; } = OperatingSystem.IsWindows() ? CreateWindows() : CreatePortable();

    [SupportedOSPlatform("windows")]
    private static Platforms CreateWindows() =>
        new(new WindowsFileSystem(), new WindowsRecycleBin(), new WindowsFileLocks(), new WindowsDrives());

    private static Platforms CreatePortable() =>
        new(new PortableFileSystem(), new PortableRecycleBin(), new PortableFileLocks(), new PortableDrives());
}
