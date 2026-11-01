using System.Globalization;

namespace HabilisTerminal.Core.FileSystem;

/// <summary>Far sort modes available in v1 (SPEC §6).</summary>
public enum SortMode
{
    Name,
    Extension,
    Modified,
    Size,
    Unsorted,
    Created,
    Accessed,
}

public sealed record SortOptions(SortMode Mode = SortMode.Name, bool Descending = false, bool DirectoriesFirst = true);

/// <summary>
/// Sorts listings. Names compare like Explorer (<c>file2</c> &lt; <c>file10</c>, case-insensitive, culture-aware)
/// through <see cref="CompareOptions.NumericOrdering"/>; ties fall back to the name, then to enumeration order.
/// Culture-aware comparison dominates the cost, so names are turned into sort keys once per entry and the sort
/// compares bytes (same order as <see cref="CompareInfo.Compare(string, string, CompareOptions)"/>).
/// </summary>
public static class EntrySorter
{
    private const CompareOptions NameCompareOptions = CompareOptions.IgnoreCase | CompareOptions.NumericOrdering;

    /// <summary>
    /// Returns a new sorted array; the input (enumeration order) is left untouched. <paramref name="folderSize"/>
    /// gives the known sizes of folders for sorting by size (unknown ones count as empty).
    /// </summary>
    public static FileEntry[] Sort(IReadOnlyList<FileEntry> entries, SortOptions options, CultureInfo? culture = null, Func<FileEntry, long?>? folderSize = null)
    {
        var compareInfo = (culture ?? CultureInfo.CurrentCulture).CompareInfo;
        var count = entries.Count;
        var order = new int[count];
        for (var i = 0; i < count; i++)
        {
            order[i] = i;
        }

        if (options.Mode != SortMode.Unsorted || options.DirectoriesFirst)
        {
            var keys = new SortKeys(entries, options.Mode, compareInfo, folderSize);

            // Index sort keeps the comparison stable: ties are resolved by enumeration order.
            Array.Sort(order, (a, b) => Compare(entries[a], entries[b], a, b, options, keys));
        }

        var result = new FileEntry[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = entries[order[i]];
        }

        return result;
    }

    public static int CompareNames(string x, string y, CompareInfo compareInfo) =>
        compareInfo.Compare(x, y, NameCompareOptions);

    private static int Compare(in FileEntry x, in FileEntry y, int xIndex, int yIndex, SortOptions options, SortKeys keys)
    {
        if (options.DirectoriesFirst && x.IsDirectory != y.IsDirectory)
        {
            return x.IsDirectory ? -1 : 1;
        }

        var result = options.Mode switch
        {
            SortMode.Name => keys.CompareNames(xIndex, yIndex),
            SortMode.Extension => keys.CompareExtensions(xIndex, yIndex),
            SortMode.Modified => x.LastWriteTimeUtc.CompareTo(y.LastWriteTimeUtc),
            SortMode.Size => keys.CompareSizes(xIndex, yIndex, x, y),
            SortMode.Created => x.CreationTimeUtc.CompareTo(y.CreationTimeUtc),
            SortMode.Accessed => x.LastAccessTimeUtc.CompareTo(y.LastAccessTimeUtc),
            _ => 0,
        };

        if (result == 0 && options.Mode != SortMode.Unsorted && options.Mode != SortMode.Name)
        {
            result = keys.CompareNames(xIndex, yIndex);
        }

        if (options.Descending)
        {
            result = -result;
        }

        return result != 0 ? result : xIndex.CompareTo(yIndex);
    }

    /// <summary>Precomputed culture sort keys for names (and extensions in extension mode).</summary>
    private sealed class SortKeys
    {
        private readonly byte[][]? _names;
        private readonly byte[][]? _extensions;
        private readonly long[]? _sizes;

        public SortKeys(IReadOnlyList<FileEntry> entries, SortMode mode, CompareInfo compareInfo, Func<FileEntry, long?>? folderSize)
        {
            if (mode == SortMode.Unsorted)
            {
                return;
            }

            _names = new byte[entries.Count][];
            for (var i = 0; i < entries.Count; i++)
            {
                _names[i] = compareInfo.GetSortKey(entries[i].Name, NameCompareOptions).KeyData;
            }

            if (mode == SortMode.Extension)
            {
                _extensions = new byte[entries.Count][];
                for (var i = 0; i < entries.Count; i++)
                {
                    _extensions[i] = compareInfo.GetSortKey(entries[i].Extension.ToString(), NameCompareOptions).KeyData;
                }
            }

            if (mode == SortMode.Size && folderSize is not null)
            {
                _sizes = new long[entries.Count];
                for (var i = 0; i < entries.Count; i++)
                {
                    _sizes[i] = entries[i].IsDirectory ? folderSize(entries[i]) ?? 0 : entries[i].Length;
                }
            }
        }

        public int CompareSizes(int x, int y, in FileEntry xEntry, in FileEntry yEntry) =>
            _sizes is null ? xEntry.Length.CompareTo(yEntry.Length) : _sizes[x].CompareTo(_sizes[y]);

        public int CompareNames(int x, int y) => _names is null ? 0 : _names[x].AsSpan().SequenceCompareTo(_names[y]);

        public int CompareExtensions(int x, int y)
        {
            var result = _extensions is null ? 0 : _extensions[x].AsSpan().SequenceCompareTo(_extensions[y]);
            return result != 0 ? result : CompareNames(x, y);
        }
    }
}
