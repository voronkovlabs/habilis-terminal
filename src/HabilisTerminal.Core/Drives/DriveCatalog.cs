using HabilisTerminal.Core.Platform;

namespace HabilisTerminal.Core.Drives;

public sealed record DriveSummary(
    string Root,
    DriveType Type,
    bool IsReady,
    string? Label = null,
    string? FileSystem = null,
    long? TotalBytes = null,
    long? FreeBytes = null)
{
    public double? UsedFraction => TotalBytes is > 0 && FreeBytes is { } free ? 1.0 - ((double)free / TotalBytes.Value) : null;
}

/// <summary>
/// Drives and well-known places for the drive menu (SPEC §6). Listing is instant; details are queried per drive
/// with a timeout, because network, optical and disconnected drives can block for a long time.
/// </summary>
public static class DriveCatalog
{
    public static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(2);

    public static IReadOnlyList<DriveSummary> List() =>
        DriveInfo.GetDrives().Where(d => IsShown(d.Name)).Select(d => new DriveSummary(d.Name, d.DriveType, IsReady: false)).ToArray();

    /// <summary>
    /// On a Mac only what the Finder shows (issue #274): the start disk and the volumes under /Volumes, not /dev, the
    /// system's own volumes or mounted assets.
    /// </summary>
    internal static bool IsShown(string root) => !OperatingSystem.IsMacOS() || IsMacVolume(root);

    internal static bool IsMacVolume(string root) =>
        root == "/" || root.StartsWith("/Volumes/", StringComparison.Ordinal) && root.TrimEnd('/').Length > "/Volumes".Length;

    /// <summary>The start disk as the Finder names it ("Macintosh HD"): the entry of /Volumes that leads to / (#274).</summary>
    private static string? MacStartDiskName()
    {
        try
        {
            return new DirectoryInfo("/Volumes").EnumerateDirectories()
                .FirstOrDefault(d => d.LinkTarget is "/" or "/System/Volumes/Data")?.Name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The trash's name as the system calls it: the Trash on a Mac, the Recycle Bin on Windows.</summary>
    public static string TrashName => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

    /// <summary>Reads label, file system and sizes; returns the input unchanged when the drive does not answer in time.</summary>
    public static async Task<DriveSummary> QueryAsync(DriveSummary drive, TimeSpan? timeout = null)
    {
        var query = Task.Run(() =>
        {
            try
            {
                var info = new DriveInfo(drive.Root);
                return info.IsReady
                    ? drive with
                    {
                        IsReady = true,
                        Label = OperatingSystem.IsMacOS() && drive.Root == "/" ? MacStartDiskName() ?? info.VolumeLabel : info.VolumeLabel,
                        FileSystem = info.DriveFormat,
                        TotalBytes = info.TotalSize,
                        FreeBytes = info.AvailableFreeSpace,
                    }
                    : drive;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return drive;
            }
        });

        var finished = await Task.WhenAny(query, Task.Delay(timeout ?? DefaultQueryTimeout)).ConfigureAwait(false);
        return finished == query ? await query.ConfigureAwait(false) : drive;
    }

    /// <summary>Home, Desktop, Downloads, Documents — those that exist; on a Mac also Applications and iCloud Drive (#274).</summary>
    public static IReadOnlyList<(string Name, string Path)> Places()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var places = new List<(string, string)>();
        if (OperatingSystem.IsMacOS())
        {
            places.Add(("Applications", "/Applications"));
        }

        places.Add(("Home", home));
        places.Add(("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)));

        if (Platforms.Current.Drives.DownloadsFolder() is { } downloads)
        {
            places.Add(("Downloads", downloads));
        }

        places.Add(("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        if (OperatingSystem.IsMacOS() && home.Length > 0)
        {
            places.Add(("iCloud Drive", Path.Combine(home, "Library", "Mobile Documents", "com~apple~CloudDocs")));
        }

        return places.Where(p => !string.IsNullOrEmpty(p.Item2) && Directory.Exists(p.Item2)).ToArray();
    }

    /// <summary>Disconnects a mapped network drive (Del in the drive menu).</summary>
    public static void DisconnectNetworkDrive(string root) => Platforms.Current.Drives.DisconnectNetworkDrive(root);
}
