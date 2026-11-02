using System.IO;
using System.Globalization;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Panels;

namespace HabilisTerminal.App.Panels;

public enum ColumnKind
{
    Name,
    Size,
    Date,
    Time,
    Created,
    Modified,
    Accessed,
    Attributes,
    CompactSize,
    ModifiedShort,

    /// <summary>A plugin panel's own column (issue #49); <see cref="ColumnSpec.Key"/> names it.</summary>
    Custom,
}

public sealed record ColumnSpec(ColumnKind Kind, string Title, double DefaultWidth, bool AlignRight, SortMode? SortMode, string? Key = null);

/// <summary>Columns per view mode and how cells are formatted (SPEC §6).</summary>
public static class PanelColumns
{
    public static readonly ColumnSpec Name = new(ColumnKind.Name, "Name", 0, false, SortMode.Name);
    private static readonly ColumnSpec Size = new(ColumnKind.Size, "Size", 104, true, SortMode.Size);
    private static readonly ColumnSpec Date = new(ColumnKind.Date, "Date", 84, true, SortMode.Modified);
    private static readonly ColumnSpec Time = new(ColumnKind.Time, "Time", 52, true, SortMode.Modified);
    private static readonly ColumnSpec Created = new(ColumnKind.Created, "Created", 128, true, SortMode.Created);
    private static readonly ColumnSpec Modified = new(ColumnKind.Modified, "Modified", 128, true, SortMode.Modified);
    private static readonly ColumnSpec Accessed = new(ColumnKind.Accessed, "Accessed", 128, true, SortMode.Accessed);
    private static readonly ColumnSpec Attributes = new(ColumnKind.Attributes, "Attr", 56, false, null);
    private static readonly ColumnSpec CompactSize = new(ColumnKind.CompactSize, "Size", 72, true, SortMode.Size);
    private static readonly ColumnSpec MediumSize = new(ColumnKind.CompactSize, "Size", 76, true, SortMode.Size);
    private static readonly ColumnSpec ModifiedShort = new(ColumnKind.ModifiedShort, "Modified", 128, true, SortMode.Modified);

    /// <summary>Number of name columns for the multi-column modes; 1 for table modes.</summary>
    public static int NameColumnCount(PanelViewMode mode) => mode switch
    {
        PanelViewMode.Brief => 3,
        PanelViewMode.Medium => 2,
        _ => 1,
    };

    /// <summary>Name columns of a panel: one for a plugin panel (issue #49), else by view mode.</summary>
    public static int NameColumnCount(PanelState state) => state.Extension is null ? NameColumnCount(state.ViewMode) : 1;

    /// <summary>Detail columns of a panel: a plugin panel's layout (issue #49), else by view mode.</summary>
    public static IReadOnlyList<ColumnSpec> DetailColumns(PanelState state) =>
        state.Extension is { } extension
            ? extension.Columns.Select(c => c.Key switch
            {
                "size" => Size with { Title = c.Title },
                "modified" => Modified with { Title = c.Title },
                _ => new ColumnSpec(ColumnKind.Custom, c.Title, c.Width, c.AlignRight, null, c.Key),
            }).ToArray()
            : DetailColumns(state.ViewMode);

    /// <summary>Columns after the name: for table modes, and after each name column in Medium.</summary>
    public static IReadOnlyList<ColumnSpec> DetailColumns(PanelViewMode mode) => mode switch
    {
        PanelViewMode.Medium => [MediumSize],
        PanelViewMode.Full => [Size, Date, Time],
        PanelViewMode.Wide => [Size],
        PanelViewMode.Detailed => [Size, Created, Modified, Accessed, Attributes],
        PanelViewMode.AlternativeFull => [CompactSize, ModifiedShort],
        _ => [],
    };

    /// <summary>
    /// Text of a cell. <paramref name="folderSize"/> is the folder's known size; <paramref name="autoSized"/> says
    /// the panel sizes folders in the background, so a folder not sized yet shows nothing instead of "Folder".
    /// </summary>
    public static string FormatCell(ColumnKind kind, PanelItem item, CultureInfo culture, FolderSize? folderSize = null, bool autoSized = false)
    {
        var entry = item.Entry;
        return kind switch
        {
            ColumnKind.Size or ColumnKind.CompactSize when item.IsParent => "Up",
            ColumnKind.Size when folderSize is { } size => SizeFormat.Folder(size, compact: false, culture),
            ColumnKind.CompactSize when folderSize is { } size => SizeFormat.Folder(size, compact: true, culture),
            ColumnKind.Size or ColumnKind.CompactSize when entry.IsDirectory => entry.IsReparsePoint ? "Link" : autoSized ? string.Empty : "Folder",
            ColumnKind.Size => entry.Length.ToString("N0", culture),
            ColumnKind.CompactSize => FormatCompactSize(entry.Length, culture),
            _ when item.IsParent => string.Empty,
            // A plugin entry without a time (an archive folder implied by paths, issue #51) shows none.
            ColumnKind.Date or ColumnKind.Time or ColumnKind.Created or ColumnKind.Modified or ColumnKind.ModifiedShort or ColumnKind.Accessed
                when entry.LastWriteTimeUtc == DateTime.MinValue => string.Empty,
            ColumnKind.Date => entry.LastWriteTimeUtc.ToLocalTime().ToString("d", culture),
            ColumnKind.Time => entry.LastWriteTimeUtc.ToLocalTime().ToString("t", culture),
            ColumnKind.Created => entry.CreationTimeUtc.ToLocalTime().ToString("g", culture),
            ColumnKind.Modified or ColumnKind.ModifiedShort => entry.LastWriteTimeUtc.ToLocalTime().ToString("g", culture),
            ColumnKind.Accessed => entry.LastAccessTimeUtc.ToLocalTime().ToString("g", culture),
            ColumnKind.Attributes => FormatAttributes(entry.Attributes),
            _ => item.Name,
        };
    }

    public static string FormatCompactSize(long bytes, CultureInfo culture) => SizeFormat.Compact(bytes, culture);

    /// <summary>Far-style attribute letters: R A H S C E (read-only, archive, hidden, system, compressed, encrypted) plus L for links.</summary>
    public static string FormatAttributes(FileAttributes attributes)
    {
        (FileAttributes Flag, char Letter)[] map =
        [
            (FileAttributes.ReadOnly, 'R'), (FileAttributes.Archive, 'A'), (FileAttributes.Hidden, 'H'),
            (FileAttributes.System, 'S'), (FileAttributes.Compressed, 'C'), (FileAttributes.Encrypted, 'E'),
            (FileAttributes.ReparsePoint, 'L'),
        ];
        return new string(map.Where(m => (attributes & m.Flag) != 0).Select(m => m.Letter).ToArray());
    }
}
