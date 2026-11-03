namespace HabilisTerminal.Plugins;

/// <summary>
/// Progress of an operation, shown by the host with a Cancel button. Thread-safe: parallel transfers may call it
/// from several threads.
/// </summary>
public interface IOperationProgress
{
    /// <summary>Totals when known (null: unknown).</summary>
    void SetTotals(long? items, long? bytes);

    /// <summary>The item being worked on (shown as the current path).</summary>
    void Begin(string item);

    /// <summary>Bytes transferred since the last call.</summary>
    void AddBytes(long bytes);

    /// <summary>One item finished.</summary>
    void CompleteItem();
}

/// <summary>What to do when a target already exists.</summary>
public enum OverwriteChoice
{
    Overwrite,
    Skip,
    OverwriteAll,
    SkipAll,
    Cancel,
}

/// <summary>A copy or move between a plugin panel and a local folder.</summary>
public sealed class TransferRequest(IReadOnlyList<PanelEntry> entries, string localFolder, bool move, Func<string, Task<OverwriteChoice>> askOverwrite)
{
    /// <summary>The plugin entries: the sources of a download, unused for an upload.</summary>
    public IReadOnlyList<PanelEntry> Entries { get; } = entries;

    /// <summary>The local folder: the target of a download.</summary>
    public string LocalFolder { get; } = localFolder;

    /// <summary>Delete the sources after a successful copy (F6).</summary>
    public bool Move { get; } = move;

    /// <summary>Asks the user about an existing target (by its name or path).</summary>
    public Func<string, Task<OverwriteChoice>> AskOverwrite { get; } = askOverwrite;
}

/// <summary>A summary the host reports after an operation.</summary>
public sealed record OperationOutcome(int Succeeded, int Skipped, int Failed, string? Message = null);

/// <summary>Downloads entries to a local folder (F5 from the plugin panel, and F3 through a temporary copy).</summary>
public interface IGetFiles
{
    /// <summary>F6 (move) from the plugin panel is offered.</summary>
    bool CanMove { get; }

    /// <summary>
    /// Copies <see cref="TransferRequest.Entries"/> into <see cref="TransferRequest.LocalFolder"/>. A failed or
    /// cancelled transfer must leave no partial file behind.
    /// </summary>
    Task<OperationOutcome> GetFilesAsync(TransferRequest request, IOperationProgress progress, CancellationToken cancellationToken);
}

/// <summary>Uploads local files and folders into the current location (F5 / F6 into the plugin panel).</summary>
public interface IPutFiles
{
    /// <summary>
    /// Copies <paramref name="localPaths"/> (files and folders, recursively) into the current location. For a move
    /// the host deletes the local originals after a successful upload.
    /// </summary>
    Task<OperationOutcome> PutFilesAsync(IReadOnlyList<string> localPaths, Func<string, Task<OverwriteChoice>> askOverwrite, IOperationProgress progress, CancellationToken cancellationToken);
}

/// <summary>Deletes entries (F8). The host confirms first, listing the entries.</summary>
public interface IDeleteEntries
{
    Task<OperationOutcome> DeleteAsync(IReadOnlyList<PanelEntry> entries, IOperationProgress progress, CancellationToken cancellationToken);
}

/// <summary>Makes a folder in the current location (F7).</summary>
public interface IMakeFolder
{
    Task MakeFolderAsync(string name, CancellationToken cancellationToken);
}

/// <summary>A plugin-made view (F3), e.g. a job summary. Takes precedence over viewing a copy from <see cref="IGetFiles"/>.</summary>
public interface IViewEntry
{
    /// <summary>The title and text to show, or null to fall back to the default (a temporary copy, if possible).</summary>
    Task<(string Title, string Text)?> ViewAsync(PanelEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// Ctrl+Q for a plugin panel (SDK 1.1): the text the other panel shows for the entry under the cursor, in a monospace
/// font (block characters such as ▁▂▃▄▅▆▇█ draw charts). Asked on every cursor move and listing refresh, so it should
/// come from data the panel already has.
/// </summary>
public interface IQuickViewEntry
{
    /// <summary>The text, or null for an empty quick view.</summary>
    Task<string?> QuickViewAsync(PanelEntry entry, CancellationToken cancellationToken);
}

/// <summary>Commands of a panel: F9 > Panel lists them; a <see cref="PanelCommand.Key"/> runs one directly.</summary>
public interface IPanelCommands
{
    IReadOnlyList<PanelCommand> Commands { get; }
}

/// <summary>
/// A panel command. <see cref="Key"/> uses the host's notation, e.g. "F8", "Shift+F3", "Ctrl+F"; null: menu only.
/// A command key takes precedence over the host's own key in this panel.
/// </summary>
public sealed record PanelCommand(string Title, string? Key, Func<PanelCommandContext, Task> Run);

/// <summary>What a command works on.</summary>
public sealed class PanelCommandContext(PanelEntry? current, IReadOnlyList<PanelEntry> selected, IOperationProgress progress, CancellationToken cancellationToken)
{
    /// <summary>The entry under the cursor (null on "..").</summary>
    public PanelEntry? Current { get; } = current;

    /// <summary>The marked entries, or the entry under the cursor when nothing is marked.</summary>
    public IReadOnlyList<PanelEntry> Selected { get; } = selected;

    public IOperationProgress Progress { get; } = progress;

    public CancellationToken CancellationToken { get; } = cancellationToken;
}
