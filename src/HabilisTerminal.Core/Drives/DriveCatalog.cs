using System.Runtime.InteropServices;

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
        DriveInfo.GetDrives().Select(d => new DriveSummary(d.Name, d.DriveType, IsReady: false)).ToArray();

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
                        Label = info.VolumeLabel,
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

    /// <summary>Home, Desktop, Downloads, Documents — those that exist.</summary>
    public static IReadOnlyList<(string Name, string Path)> Places()
    {
        var places = new List<(string, string)>
        {
            ("Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
        };

        if (KnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B")) is { } downloads)
        {
            places.Add(("Downloads", downloads));
        }

        places.Add(("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        return places.Where(p => !string.IsNullOrEmpty(p.Item2) && Directory.Exists(p.Item2)).ToArray();
    }

    /// <summary>Disconnects a mapped network drive (Del in the drive menu).</summary>
    public static void DisconnectNetworkDrive(string root)
    {
        var name = root.TrimEnd('\\');
        var result = WNetCancelConnection2(name, ConnectUpdateProfile, true);
        if (result != 0)
        {
            throw new IOException(new System.ComponentModel.Win32Exception(result).Message);
        }
    }

    private const int ConnectUpdateProfile = 0x1;

    private static string? KnownFolderPath(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var pointer) != 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string lpName, int dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fForce);
}
