using System.Globalization;
using HabilisTerminal.Core.FileSystem;

namespace HabilisTerminal.Core.Panels;

/// <summary>Far view modes in v1 (SPEC §6).</summary>
public enum PanelViewMode
{
    /// <summary>Ctrl+1: names in several columns.</summary>
    Brief,

    /// <summary>Ctrl+2: names in two columns.</summary>
    Medium,

    /// <summary>Ctrl+3: name, size, date, time.</summary>
    Full,

    /// <summary>Ctrl+4: name and size.</summary>
    Wide,

    /// <summary>Ctrl+5: name, size, all dates, attributes.</summary>
    Detailed,

    /// <summary>Ctrl+0: name, compact size, modified.</summary>
    AlternativeFull,
}

/// <summary>A row of a panel: the ".." entry or a directory entry.</summary>
public readonly record struct PanelItem(FileEntry Entry, bool IsParent)
{
    public const string ParentName = "..";

    public string Name => IsParent ? ParentName : Entry.Name;
}

/// <summary>
/// Everything a file panel shows, without UI: directory, sorted items (with ".." first), cursor, selection,
/// sort and view options (SPEC §5, §6). Keeps the raw listing so sorting and hidden-file filtering can change
/// without re-reading the directory.
/// </summary>
public sealed class PanelState
{
    private FileEntry[] _raw = [];
    private FileEntry[] _items = [];

    public string Path { get; private set; } = string.Empty;

    public bool HasParent { get; private set; }

    public int Count => _items.Length + (HasParent ? 1 : 0);

    public int CursorIndex { get; private set; }

    public Selection Selection { get; } = new();

    /// <summary>Folder sizes computed with F3, shown in the size column until the directory changes.</summary>
    public Dictionary<string, long> FolderSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SortOptions Sort { get; private set; } = new();

    /// <summary>Shift+F12: selected entries go first.</summary>
    public bool SelectedFirst { get; private set; }

    /// <summary>Ctrl+H.</summary>
    public bool ShowHidden { get; private set; } = true;

    public PanelViewMode ViewMode { get; set; } = PanelViewMode.Full;

    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;

    /// <summary>The listing as read (enumeration order, hidden entries included).</summary>
    public IReadOnlyList<FileEntry> RawEntries => _raw;

    /// <summary>All visible entries (without ".."), in display order.</summary>
    public IReadOnlyList<FileEntry> Entries => _items;

    public PanelItem Current => ItemAt(CursorIndex);

    public PanelItem ItemAt(int index) =>
        HasParent && index == 0
            ? new PanelItem(default, IsParent: true)
            : new PanelItem(_items[index - (HasParent ? 1 : 0)], IsParent: false);

    /// <summary>
    /// Shows a (new or refreshed) listing. The cursor stays on <paramref name="focusName"/> when given and present,
    /// otherwise on the previous name, otherwise at the same index. Selection is kept for names that still exist.
    /// </summary>
    public void SetListing(string path, IReadOnlyList<FileEntry> rawEntries, string? focusName = null)
    {
        var samePath = string.Equals(path, Path, StringComparison.OrdinalIgnoreCase);
        var previousName = Count > 0 ? Current.Name : null;
        var previousIndex = CursorIndex;

        Path = path;
        HasParent = System.IO.Path.GetDirectoryName(path.TrimEnd('\\')) is not null && !IsUncRoot(path);
        _raw = rawEntries as FileEntry[] ?? rawEntries.ToArray();
        if (samePath)
        {
            Selection.RetainExisting(_raw);
        }
        else
        {
            Selection.Clear();
            FolderSizes.Clear();
        }

        Rebuild();

        if (!(focusName is not null && TrySetCursor(focusName)) &&
            !(samePath && previousName is not null && TrySetCursor(previousName)))
        {
            CursorIndex = samePath ? Math.Clamp(previousIndex, 0, Math.Max(0, Count - 1)) : 0;
        }
    }

    public void SetSort(SortOptions sort)
    {
        Sort = sort;
        RebuildKeepingCursor();
    }

    /// <summary>Far: pressing the active sort key again reverses the direction.</summary>
    public void ApplySortKey(SortMode mode) =>
        SetSort(Sort.Mode == mode ? Sort with { Descending = !Sort.Descending } : Sort with { Mode = mode, Descending = false });

    public void SetSelectedFirst(bool value)
    {
        SelectedFirst = value;
        RebuildKeepingCursor();
    }

    public void SetShowHidden(bool value)
    {
        ShowHidden = value;
        RebuildKeepingCursor();
    }

    public void MoveCursor(int delta) => SetCursor(CursorIndex + delta);

    public void SetCursor(int index) => CursorIndex = Count == 0 ? 0 : Math.Clamp(index, 0, Count - 1);

    public bool TrySetCursor(string name)
    {
        if (HasParent && name == PanelItem.ParentName)
        {
            CursorIndex = 0;
            return true;
        }

        for (var i = 0; i < _items.Length; i++)
        {
            if (string.Equals(_items[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                CursorIndex = i + (HasParent ? 1 : 0);
                return true;
            }
        }

        return false;
    }

    /// <summary>Ins: toggles the entry under the cursor (never ".."), then moves down.</summary>
    public void ToggleCurrentAndMoveDown()
    {
        if (!Current.IsParent && Count > 0)
        {
            Selection.Toggle(Current.Name);
        }

        MoveCursor(1);
    }

    /// <summary>Entries an operation applies to: the selection, or the entry under the cursor when nothing is selected.</summary>
    public IReadOnlyList<FileEntry> TargetEntries()
    {
        if (Selection.Count > 0)
        {
            return _items.Where(e => Selection.Contains(e.Name)).ToArray();
        }

        return Count > 0 && !Current.IsParent ? [Current.Entry] : [];
    }

    /// <summary>
    /// Quick search (Alt+letter): the next item whose name starts with <paramref name="prefix"/>
    /// (Far wildcards allowed), searching from <paramref name="startIndex"/> and wrapping around.
    /// </summary>
    public int FindNext(string prefix, int startIndex, int direction = 1)
    {
        if (Count == 0 || prefix.Length == 0)
        {
            return -1;
        }

        var hasWildcards = prefix.AsSpan().IndexOfAny('*', '?') >= 0;
        FileMask? mask = null;
        if (hasWildcards && !FileMask.TryParse(prefix.TrimEnd('*') + "*", out mask))
        {
            return -1;
        }

        for (var step = 0; step < Count; step++)
        {
            var index = ((startIndex + (step * direction)) % Count + Count) % Count;
            var name = ItemAt(index).Name;
            var matches = mask?.IsMatch(name) ?? name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            if (matches)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsUncRoot(string path)
    {
        if (!LongPath.IsUnc(path))
        {
            return false;
        }

        // \\server\share is the top of a UNC path.
        var parts = path.TrimStart('\\').TrimEnd('\\').Split('\\');
        return parts.Length <= 2;
    }

    private void RebuildKeepingCursor()
    {
        var name = Count > 0 ? Current.Name : null;
        Rebuild();
        if (name is null || !TrySetCursor(name))
        {
            SetCursor(CursorIndex);
        }
    }

    private void Rebuild()
    {
        IReadOnlyList<FileEntry> visible = ShowHidden ? _raw : _raw.Where(e => !e.IsHidden && !e.IsSystem).ToArray();
        var sorted = EntrySorter.Sort(visible, Sort, Culture);
        if (SelectedFirst && Selection.Count > 0)
        {
            // Stable partition; directories-first is kept inside each part.
            sorted = [.. sorted.Where(e => Selection.Contains(e.Name)), .. sorted.Where(e => !Selection.Contains(e.Name))];
        }

        _items = sorted;
    }
}
