using HabilisTerminal.Core.FileSystem;

namespace HabilisTerminal.Core.Operations;

/// <summary>
/// Far "Compare folders" (SPEC §8): top level only; in each panel select files that are missing in the other panel
/// or newer than their counterpart; directories are compared by presence.
/// </summary>
public static class FolderComparer
{
    /// <summary>FAT and exFAT keep modification times with 2-second precision.</summary>
    public static readonly TimeSpan FatTolerance = TimeSpan.FromSeconds(2);

    public static (IReadOnlyList<string> Left, IReadOnlyList<string> Right) Compare(
        IReadOnlyList<FileEntry> left,
        IReadOnlyList<FileEntry> right,
        bool compareSize = false,
        bool fatTimePrecision = false)
    {
        var tolerance = fatTimePrecision ? FatTolerance : TimeSpan.Zero;
        var rightByName = right.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var leftByName = left.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var selectLeft = new List<string>();
        var selectRight = new List<string>();

        foreach (var entry in left)
        {
            if (!rightByName.TryGetValue(entry.Name, out var other) || entry.IsDirectory != other.IsDirectory)
            {
                selectLeft.Add(entry.Name);
            }
            else if (!entry.IsDirectory)
            {
                var difference = entry.LastWriteTimeUtc - other.LastWriteTimeUtc;
                if (difference > tolerance)
                {
                    selectLeft.Add(entry.Name);
                }
                else if (difference < -tolerance)
                {
                    selectRight.Add(other.Name);
                }
                else if (compareSize && entry.Length != other.Length)
                {
                    // Same time, different size: mark both, the user decides.
                    selectLeft.Add(entry.Name);
                    selectRight.Add(other.Name);
                }
            }
        }

        foreach (var entry in right)
        {
            if (!leftByName.TryGetValue(entry.Name, out var other) || entry.IsDirectory != other.IsDirectory)
            {
                selectRight.Add(entry.Name);
            }
        }

        return (selectLeft, selectRight);
    }

    /// <summary>True when the path is on a FAT / exFAT volume (2-second timestamps).</summary>
    public static bool IsFatVolume(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is not null && new DriveInfo(root).DriveFormat.StartsWith("FAT", StringComparison.OrdinalIgnoreCase)
                || (root is not null && new DriveInfo(root).DriveFormat.Equals("exFAT", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
