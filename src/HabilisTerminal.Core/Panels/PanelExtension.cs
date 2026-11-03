using HabilisTerminal.Core.FileSystem;

namespace HabilisTerminal.Core.Panels;

/// <summary>A column a plugin panel adds after the name (docs/plugins.md §4). Width is at the default font size.</summary>
public sealed record ExtensionColumn(string Key, string Title, double Width, bool AlignRight);

/// <summary>
/// What a plugin panel adds to a <see cref="PanelState"/>: custom columns, groups sorted first, row tones and sorting
/// by a custom column (issue #49). The listing itself stays a list of <see cref="FileEntry"/>; these callbacks look the
/// plugin's data up by entry.
/// </summary>
public sealed class PanelExtension
{
    /// <summary>The current layout's columns.</summary>
    public IReadOnlyList<ExtensionColumn> Columns { get; set; } = [];

    /// <summary>A custom cell: text and sort value, or null when the entry has no value for the column.</summary>
    public Func<FileEntry, string, (string Text, IComparable? SortValue)?> Cell { get; init; } = (_, _) => null;

    /// <summary>Sorted first, lowest first (e.g. running jobs).</summary>
    public Func<FileEntry, int> Group { get; init; } = _ => 0;

    /// <summary>The row tone, as the SDK's <c>RowTone</c> number (0 = normal).</summary>
    public Func<FileEntry, int> Tone { get; init; } = _ => 0;

    /// <summary>A named host icon for the row (<c>PanelEntry.Icon</c>), or null for the usual file or folder icon.</summary>
    public Func<FileEntry, string?> Icon { get; init; } = _ => null;

    /// <summary>The custom column the panel is sorted by; null: the panel's <see cref="SortOptions"/>.</summary>
    public string? SortKey { get; set; }

    public bool SortDescending { get; set; }

    /// <summary>Orders an already sorted listing by group, then by <see cref="SortKey"/> (folders first); stable.</summary>
    public FileEntry[] Order(FileEntry[] sorted)
    {
        IOrderedEnumerable<FileEntry> ordered = sorted.OrderBy(Group);
        if (SortKey is { } key)
        {
            // Folders first, and entries without a value last in either direction.
            ordered = ordered.ThenBy(e => e.IsDirectory ? 0 : 1).ThenBy(e => Cell(e, key)?.SortValue is null ? 1 : 0);
            var comparer = Comparer<IComparable?>.Create(CompareValues);
            ordered = SortDescending
                ? ordered.ThenByDescending(e => Cell(e, key)?.SortValue, comparer)
                : ordered.ThenBy(e => Cell(e, key)?.SortValue, comparer);
        }

        return [.. ordered];
    }

    /// <summary>Missing values sort last; values of different types compare by their text.</summary>
    public static int CompareValues(IComparable? x, IComparable? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : 1) : -1;
        }

        if (x.GetType() == y.GetType())
        {
            return x.CompareTo(y);
        }

        return x is IConvertible && y is IConvertible && IsNumber(x) && IsNumber(y)
            ? Convert.ToDouble(x, System.Globalization.CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(y, System.Globalization.CultureInfo.InvariantCulture))
            : string.CompareOrdinal(x.ToString(), y.ToString());
    }

    private static bool IsNumber(object value) => value is byte or short or int or long or float or double or decimal or uint or ulong or ushort or sbyte;
}
