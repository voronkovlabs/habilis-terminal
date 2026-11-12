using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using HabilisTerminal.Core.Interop;

namespace HabilisTerminal.Core.Platform.Portable;

/// <summary>
/// File calls with plain .NET, for systems other than Windows (#259). Symbolic links are reported in the Windows
/// reparse format, so <see cref="FileSystem.LinkInfo"/> reads them one way; NTFS compression and encryption do not exist.
/// </summary>
internal sealed class PortableFileSystem : IFileSystemNative
{
    private const int BufferSize = 1024 * 1024;

    public void CopyFile(string source, string target, bool copyLinkItself, bool failIfExists, Func<long, long, bool> progress)
    {
        if (failIfExists && (File.Exists(target) || Directory.Exists(target) || new FileInfo(target).LinkTarget is not null))
        {
            throw new Win32Exception(NativeFile.ErrorFileExists);
        }

        var info = new FileInfo(source);
        if (copyLinkItself && info.LinkTarget is { } link)
        {
            File.Delete(target);
            File.CreateSymbolicLink(target, link);
            return;
        }

        var total = info.Length;
        var done = 0L;
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize))
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            {
                var buffer = new byte[BufferSize];
                if (!progress(0, total))
                {
                    throw new Win32Exception(NativeFile.ErrorRequestAborted);
                }

                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    done += read;
                    if (!progress(done, total))
                    {
                        throw new Win32Exception(NativeFile.ErrorRequestAborted);
                    }
                }
            }

            CopyMetadata(info, target);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == NativeFile.ErrorRequestAborted)
        {
            TryDelete(target);
            throw;
        }
    }

    public void Move(string source, string target)
    {
        var targetTaken = (File.Exists(target) || Directory.Exists(target)) && !SameName(source, target);
        if (targetTaken)
        {
            throw new Win32Exception(NativeFile.ErrorAlreadyExists);
        }

        if (!SameName(VolumeOf(source), VolumeOf(Path.GetDirectoryName(Path.GetFullPath(target)) ?? target)))
        {
            throw new Win32Exception(NativeFile.ErrorNotSameDevice);
        }

        if (Directory.Exists(source) && new DirectoryInfo(source).LinkTarget is null)
        {
            Directory.Move(source, target);
        }
        else
        {
            File.Move(source, target);
        }
    }

    public string VolumeOf(string path)
    {
        var full = Path.GetFullPath(path);
        var best = Path.GetPathRoot(full) ?? full;
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                var root = drive.RootDirectory.FullName;
                if (root.Length > best.Length && IsUnder(full, root))
                {
                    best = root;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return best;
    }

    public long? FreeBytes(string directory)
    {
        try
        {
            return new DriveInfo(VolumeOf(directory)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>There are no junctions here; a symbolic link to the folder does the same job.</summary>
    public void CreateJunction(string junction, string target) => Directory.CreateSymbolicLink(junction, Path.GetFullPath(target));

    public uint ReparseTag(string path) => LinkTarget(path) is null ? 0 : NativeFile.ReparseTagSymlink;

    /// <summary>A symbolic link as a Windows REPARSE_DATA_BUFFER (substitute and print names both the stored target).</summary>
    public byte[]? ReadReparseData(string path)
    {
        if (LinkTarget(path) is not { } target)
        {
            return null;
        }

        var name = Encoding.Unicode.GetBytes(target);
        var data = new byte[20 + (2 * name.Length)];
        BitConverter.GetBytes(NativeFile.ReparseTagSymlink).CopyTo(data, 0);
        BitConverter.GetBytes((ushort)(data.Length - 8)).CopyTo(data, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(data, 8);
        BitConverter.GetBytes((ushort)name.Length).CopyTo(data, 10);
        BitConverter.GetBytes((ushort)name.Length).CopyTo(data, 12);
        BitConverter.GetBytes((ushort)name.Length).CopyTo(data, 14);
        BitConverter.GetBytes(Path.IsPathRooted(target) ? 0u : 1u).CopyTo(data, 16);
        name.CopyTo(data, 20);
        name.CopyTo(data, 20 + name.Length);
        return data;
    }

    public int HardLinkCount(string path) => 1;

    public IReadOnlyList<string> HardLinkNames(string path) => [Path.GetFullPath(path)];

    public bool HasCompressionAndEncryption => false;

    public void SetCompression(string path, bool compressed) =>
        throw new PlatformNotSupportedException("Compression is an NTFS feature.");

    public void SetEncryption(string path, bool encrypted) =>
        throw new PlatformNotSupportedException("Encryption is an NTFS feature.");

    /// <summary>The Unix permission bits stand for the access control list.</summary>
    public void CopySecurity(string source, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(target, File.GetUnixFileMode(source));
        }
    }

    private static void CopyMetadata(FileInfo source, string target)
    {
        File.SetLastWriteTimeUtc(target, source.LastWriteTimeUtc);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(target, source.UnixFileMode);
        }
    }

    private static string? LinkTarget(string path)
    {
        try
        {
            var trimmed = Path.TrimEndingDirectorySeparator(path);
            return Directory.Exists(trimmed) ? new DirectoryInfo(trimmed).LinkTarget : new FileInfo(trimmed).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(root), StringComparison.Ordinal)
        && (path.Length == Path.TrimEndingDirectorySeparator(root).Length || root.EndsWith(Path.DirectorySeparatorChar) || path[Path.TrimEndingDirectorySeparator(root).Length] == Path.DirectorySeparatorChar);

    private static bool SameName(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>The user's trash folder (macOS: ~/.Trash; freedesktop: ~/.local/share/Trash).</summary>
internal sealed class PortableRecycleBin : IRecycleBin
{
    public string? CannotRecycleReason(string path)
    {
        if (TrashFolder() is not { } trash)
        {
            return "there is no trash folder";
        }

        var volumes = Platforms.Current.FileSystem;
        return string.Equals(volumes.VolumeOf(path), volumes.VolumeOf(trash), StringComparison.Ordinal)
            ? null
            : "the trash is on another volume";
    }

    public void Recycle(string path)
    {
        var trash = TrashFolder() ?? throw new IOException("There is no trash folder.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var files = OperatingSystem.IsMacOS() ? trash : Path.Combine(trash, "files");
        Directory.CreateDirectory(files);
        var name = Path.GetFileName(full);
        var target = Path.Combine(files, name);
        for (var n = 2; File.Exists(target) || Directory.Exists(target); n++)
        {
            target = Path.Combine(files, $"{Path.GetFileNameWithoutExtension(name)} {n}{Path.GetExtension(name)}");
        }

        if (!OperatingSystem.IsMacOS())
        {
            // freedesktop.org trash spec: an .trashinfo file lets file managers restore the item.
            var info = Path.Combine(trash, "info");
            Directory.CreateDirectory(info);
            File.WriteAllText(Path.Combine(info, Path.GetFileName(target) + ".trashinfo"),
                $"[Trash Info]\nPath={Uri.EscapeDataString(full).Replace("%2F", "/", StringComparison.Ordinal)}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
        }

        if (Directory.Exists(full) && new DirectoryInfo(full).LinkTarget is null)
        {
            Directory.Move(full, target);
        }
        else
        {
            File.Move(full, target);
        }
    }

    private static string? TrashFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return null;
        }

        return OperatingSystem.IsMacOS()
            ? Path.Combine(home, ".Trash")
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data : Path.Combine(home, ".local", "share"), "Trash");
    }
}

/// <summary>lsof, where it is installed, names the processes that hold a file.</summary>
internal sealed class PortableFileLocks : IFileLocks
{
    public string? LockingProcesses(string path)
    {
        try
        {
            using var lsof = Process.Start(new ProcessStartInfo("lsof", ["-F", "pc", "--", path])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (lsof is null)
            {
                return null;
            }

            var output = lsof.StandardOutput.ReadToEnd();
            lsof.WaitForExit(2000);
            var names = new List<string>();
            string? pid = null;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line[0] == 'p')
                {
                    pid = line[1..];
                }
                else if (line[0] == 'c' && pid is not null)
                {
                    names.Add($"{line[1..]} ({pid})");
                }
            }

            return names.Count == 0 ? null : string.Join(", ", names.Distinct());
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}

/// <summary>~/Downloads; network shares are mounted folders here, unmounted by the system's own tools.</summary>
internal sealed class PortableDrives : IDriveSource
{
    public bool CanDisconnect => false;

    public string? DownloadsFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, "Downloads");
    }

    public void DisconnectNetworkDrive(string root) =>
        throw new PlatformNotSupportedException("Unmount network shares with the system's tools.");
}
