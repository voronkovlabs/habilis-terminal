using System.Globalization;

namespace HabilisTerminal.Core.FileSystem;

/// <summary>Size texts of the panels (SPEC §6).</summary>
public static class SizeFormat
{
    /// <summary>Lower-bound marker for folders with unreadable parts.</summary>
    public const string AtLeast = "≥ ";

    /// <summary>"512 B", "1.2 KB", "34 MB".</summary>
    public static string Compact(long bytes, CultureInfo culture)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes.ToString(culture)} B" : $"{value.ToString(value < 10 ? "0.0" : "0", culture)} {units[unit]}";
    }

    /// <summary>A folder's size for a size column: exact bytes or compact, with "≥ " when parts could not be read.</summary>
    public static string Folder(FolderSize size, bool compact, CultureInfo culture) =>
        (size.IsPartial ? AtLeast : string.Empty) + (compact ? Compact(size.Bytes, culture) : size.Bytes.ToString("N0", culture));
}
