namespace HabilisTerminal.Plugins;

/// <summary>
/// One open plugin panel: a location with a listing. The host owns sorting, selection, quick search and drawing;
/// the provider lists entries and navigates. Optional operations come from <see cref="IGetFiles"/>,
/// <see cref="IPutFiles"/>, <see cref="IDeleteEntries"/>, <see cref="IMakeFolder"/>, <see cref="IViewEntry"/> and
/// <see cref="IPanelCommands"/>. The host disposes the provider when the panel leaves it.
/// </summary>
public interface IPanelProvider : IAsyncDisposable
{
    /// <summary>The panel title, e.g. "SQS · prod · eu-west-1".</summary>
    string Title { get; }

    /// <summary>
    /// The current location as an address the plugin's <see cref="Plugin.OpenAddressAsync"/> accepts, e.g.
    /// "s3://bucket/folder/". Shown as the panel path and kept in folder history.
    /// </summary>
    string Location { get; }

    /// <summary>Layouts the user switches with Ctrl+1, Ctrl+2...; the first is the default. At least one.</summary>
    IReadOnlyList<PanelLayout> Layouts { get; }

    /// <summary>Extra sort keys (Ctrl+F12 lists them with name, size and date). Usually custom columns.</summary>
    IReadOnlyList<PanelSortKey> SortKeys { get; }

    /// <summary>
    /// Lists the current location in batches: the host shows the first batch at once. Throw
    /// <see cref="PluginException"/> for errors; the host keeps the previous listing and shows the message.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<PanelEntry>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Enter on an entry. True: the location changed and the host lists it; false: nothing to enter.</summary>
    Task<bool> EnterAsync(PanelEntry entry, CancellationToken cancellationToken);

    /// <summary>Goes up one level. False at the top: the host leaves the plugin panel.</summary>
    Task<bool> GoUpAsync(CancellationToken cancellationToken);

    /// <summary>Lines for the info panel (Ctrl+L) and the panel's status line, e.g. profile, region, counts.</summary>
    IReadOnlyList<InfoLine> Info { get; }

    /// <summary>
    /// Whether the listing starts with ".." (SDK 1.2). Flat lists (queues, jobs) can do without it; the panel is still
    /// left with Ctrl+PgUp, the drive menu or folder history. Asked after each listing, so it may differ per location.
    /// </summary>
    bool ShowsParent => true;
}

/// <summary>An entry of a plugin panel.</summary>
public sealed record PanelEntry(string Name, bool IsFolder = false, long? Size = null, DateTime? Modified = null)
{
    /// <summary>Values of custom columns, by column key.</summary>
    public IReadOnlyDictionary<string, CellValue>? Cells { get; init; }

    /// <summary>Sorted before everything else, lowest first, whatever the sort key (e.g. running jobs = 0).</summary>
    public int Group { get; init; }

    /// <summary>The row colour, mapped to the host theme.</summary>
    public RowTone Tone { get; init; }

    /// <summary>
    /// An icon the host draws instead of the file or folder icon, one of <see cref="PanelIcons"/> (SDK 1.1). An
    /// unknown name falls back to the usual icon.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>The plugin's own data for this entry; the host never looks at it.</summary>
    public object? Tag { get; init; }
}

/// <summary>
/// A cell's text and the value it sorts by (a number, a <see cref="DateTime"/>, a <see cref="TimeSpan"/> or text). A null
/// <see cref="SortValue"/> marks the value as missing (e.g. text "?"): such cells sort last in either direction. A plain
/// string converts to a cell that sorts by its text.
/// </summary>
public readonly record struct CellValue(string Text, IComparable? SortValue = null)
{
    public static implicit operator CellValue(string text) => new(text, text);
}

public enum RowTone
{
    Normal,
    Accent,
    Good,
    Warning,
    Bad,
    Dim,
}

/// <summary>A layout: the columns after the name, in order.</summary>
public sealed record PanelLayout(string Title, IReadOnlyList<PanelColumn> Columns);

/// <summary>
/// A column. <see cref="Key"/> is "size" or "modified" for the built-in ones, else the key into
/// <see cref="PanelEntry.Cells"/>. <see cref="Width"/> is in pixels at the default font size.
/// </summary>
public sealed record PanelColumn(string Key, string Title, double Width = 96, bool AlignRight = false)
{
    public const string SizeKey = "size";
    public const string ModifiedKey = "modified";

    public static PanelColumn Size { get; } = new(SizeKey, "Size", 96, AlignRight: true);

    public static PanelColumn Modified { get; } = new(ModifiedKey, "Modified", 128, AlignRight: true);
}

/// <summary>A sort key over a custom column's <see cref="CellValue.SortValue"/>.</summary>
public sealed record PanelSortKey(string Key, string Title);

/// <summary>A label and value of the info panel.</summary>
public sealed record InfoLine(string Label, string Value);

/// <summary>Names of the icons the host draws for <see cref="PanelEntry.Icon"/> (SDK 1.1).</summary>
public static class PanelIcons
{
    /// <summary>A storage bucket (S3 and the like).</summary>
    public const string Bucket = "bucket";

    /// <summary>A graphics processor.</summary>
    public const string Gpu = "gpu";

    /// <summary>A running program.</summary>
    public const string Process = "process";
}
