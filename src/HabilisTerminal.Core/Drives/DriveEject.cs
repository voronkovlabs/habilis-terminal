namespace HabilisTerminal.Core.Drives;

/// <summary>
/// What ejecting a drive needs beyond the system's own verb (issue #407): which drives can be ejected, what stands on
/// them, and whether the drive has really gone.
/// </summary>
public static class DriveEject
{
    /// <summary>How long Windows gets to take the drive away before the app says it did not.</summary>
    public static readonly TimeSpan GoneWithin = TimeSpan.FromSeconds(8);

    /// <summary>USB sticks, memory cards and optical drives, as Explorer offers Eject for them.</summary>
    public static bool CanEject(DriveType type) => type is DriveType.Removable or DriveType.CDRom;

    /// <summary>The drive of a path ("E:\"); null for paths without one.</summary>
    public static string? RootOf(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            var root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? null : root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The path is on that drive: a panel or a console there holds it and must leave first.</summary>
    public static bool IsOn(string? path, string root) =>
        RootOf(path) is { } own && RootOf(root) is { } drive && string.Equals(own, drive, StringComparison.OrdinalIgnoreCase);

    /// <summary>The drive is gone: no longer listed (a USB stick), or listed without media (a card reader, an open tray).</summary>
    public static bool IsGone(string root, Func<IEnumerable<DriveInfo>>? drives = null)
    {
        var drive = (drives ?? DriveInfo.GetDrives)().FirstOrDefault(d => string.Equals(RootOf(d.Name), RootOf(root), StringComparison.OrdinalIgnoreCase));
        try
        {
            return drive is null || !drive.IsReady;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>Waits off the calling thread until <paramref name="gone"/> says so, or the time is up.</summary>
    public static async Task<bool> WaitGoneAsync(Func<bool> gone, TimeSpan within, CancellationToken cancellationToken = default)
    {
        var until = DateTime.UtcNow + within;
        while (true)
        {
            if (await Task.Run(gone, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (DateTime.UtcNow >= until)
            {
                return false;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }
}
