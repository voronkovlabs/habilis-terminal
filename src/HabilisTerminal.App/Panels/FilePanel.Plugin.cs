using System.IO;
using HabilisTerminal.App.Plugins;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Panels;
using HabilisTerminal.Core.Plugins;
using HabilisTerminal.Plugins;

namespace HabilisTerminal.App.Panels;

/// <summary>
/// A plugin panel in place of the folder listing (docs/plugins.md §5, issue #49). The provider's entries become
/// <see cref="FileEntry"/> rows, so drawing, selection, sorting and quick search stay the panel's own; custom columns,
/// groups and tones come through a <see cref="PanelExtension"/>.
/// </summary>
public sealed partial class FilePanel
{
    private PluginSession? _session;

    /// <summary>Where waiting cards of plugin work appear; set by the window.</summary>
    internal Func<OperationToasts>? WaitToasts { get; set; }

    /// <summary>The plugin location changed (folder history).</summary>
    public event Action<FilePanel, string>? PluginLocationChanged;

    /// <summary>Enter on a plugin entry that is not a folder: the window views it.</summary>
    public event Action<FilePanel, PanelEntry>? PluginEntryActivated;

    /// <summary>The panel shows a plugin rather than a folder.</summary>
    public bool IsPlugin => _session is not null;

    internal LoadedPlugin? Plugin => _session?.Plugin;

    internal IPanelProvider? Provider => _session?.Provider;

    /// <summary>The local folder the panel returns to when it leaves the plugin; the current folder otherwise.</summary>
    public string LocalPath => _session?.ReturnPath ?? State.Path;

    /// <summary>The plugin entry of a row, or null (".." or a folder listing).</summary>
    public PanelEntry? EntryOf(PanelItem item) =>
        _session is { } session && !item.IsParent && session.Entries.TryGetValue(RowKey(item.Entry), out var entry) ? entry : null;

    /// <summary>The marked plugin entries, or the entry under the cursor when nothing is marked.</summary>
    public IReadOnlyList<PanelEntry> TargetPluginEntries()
    {
        if (_session is not { } session)
        {
            return [];
        }

        return State.TargetEntries().Select(e => session.Entries.TryGetValue(RowKey(e), out var entry) ? entry : null).OfType<PanelEntry>().ToList();
    }

    /// <summary>Shows a plugin panel; false (and the folder listing back) when its first listing fails.</summary>
    /// <param name="returnPath">The folder ".." at the top leads back to; the current local folder by default.</param>
    /// <param name="returnFocus">The item the cursor goes to there (an archive's file name).</param>
    internal async Task<bool> OpenPluginAsync(LoadedPlugin plugin, IPanelProvider provider, string? returnPath = null, string? returnFocus = null)
    {
        returnPath ??= LocalPath;
        ClosePlugin();
        _watcher?.Dispose();
        _watcher = null;
        SizeWorker.Cancel(this);
        _session = new PluginSession(plugin, provider, returnPath, returnFocus);
        State.SetExtension(_session.Extension);
        ShowPluginView(plugin, provider);
        _session.ApplyLayout(0, provider);

        // The plugin's columns must not show the folder's rows while the first listing loads (issue #62).
        State.SetListing(provider.Location, [], hasParent: false);
        Refresh();
        if (await ListPluginAsync(focusName: null, opening: true))
        {
            return true;
        }

        var error = Message;
        await LeavePluginAsync();
        Message = error;
        return false;
    }

    /// <summary>Lists the provider's current location; the previous listing stays on failure.</summary>
    /// <param name="quiet">A refresh the plugin asked for itself, like a live monitor's: no waiting card.</param>
    internal async Task<bool> ListPluginAsync(string? focusName = null, bool opening = false, bool quiet = false)
    {
        if (_session is not { } session)
        {
            return false;
        }

        _loading?.Cancel();
        var loading = new CancellationTokenSource();
        _loading = loading;
        var token = loading.Token;
        var location = session.Provider.Location;
        var samePath = !opening && string.Equals(location, State.Path, StringComparison.Ordinal);
        var entries = new Dictionary<(string, bool), PanelEntry>();
        var rows = new List<FileEntry>();
        var shownFirst = false;
        Message = opening ? "Loading…" : null;
        CloseQuickSearch();
        using var wait = quiet ? null : NewWait($"Loading {location}");
        using var cancelFromCard = wait?.Token.Register(loading.Cancel) ?? default;
        try
        {
            await foreach (var batch in session.Provider.ListAsync(token).WithCancellation(token))
            {
                foreach (var entry in batch)
                {
                    var row = ToRow(entry);
                    if (entries.TryAdd(RowKey(row), entry))
                    {
                        rows.Add(row);
                    }
                }

                wait?.SetCounts($"{rows.Count:N0} items so far");

                // A large listing shows its first batch at once (a refresh keeps the old listing until it is complete).
                if (!shownFirst && !samePath && _session == session)
                {
                    shownFirst = true;
                    session.Entries = new Dictionary<(string, bool), PanelEntry>(entries);
                    session.Extension.NameColumns = NameColumns(session);
                    State.SetListing(location, rows.ToArray(), focusName, hasParent: ShowsParent(session));
                    Refresh();
                    Message = "Loading…";
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (opening && _session == session)
            {
                Message = null;
            }

            if (wait is not { Cancelled: true } || _session != session || (opening && rows.Count == 0) || samePath)
            {
                return false;
            }

            // Cancelled from the card: the provider is already at the new location, so the panel goes there too, with
            // what has arrived.
            session.Entries = entries;
            session.Extension.NameColumns = NameColumns(session);
            State.SetListing(location, rows, focusName, hasParent: ShowsParent(session));
            Refresh();
            Message = "Cancelled: the list is incomplete.";
            PluginLocationChanged?.Invoke(this, location);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_session == session)
            {
                Message = PluginManager.Describe(session.Plugin, ex);
            }

            return false;
        }

        if (token.IsCancellationRequested || _session != session)
        {
            return false;
        }

        session.Entries = entries;
        Message = null;
        session.Extension.NameColumns = NameColumns(session);
        State.SetListing(location, rows, focusName, hasParent: ShowsParent(session));
        Refresh();
        if (!samePath)
        {
            PluginLocationChanged?.Invoke(this, location);
        }

        return true;
    }

    private WaitCard? NewWait(string title) => WaitToasts is { } toasts ? new WaitCard(toasts(), title) : null;

    /// <summary>Ctrl+1, Ctrl+2...: the provider's layouts.</summary>
    private bool SetPluginLayout(int index)
    {
        if (_session is not { } session || index >= session.Provider.Layouts.Count)
        {
            return false;
        }

        session.ApplyLayout(index, session.Provider);
        State.SetExtension(session.Extension);
        InvalidatePanel();
        return true;
    }

    private async Task PluginOpenAsync(PanelItem item)
    {
        if (_session is not { } session)
        {
            return;
        }

        if (item.IsParent)
        {
            await PluginUpAsync();
            return;
        }

        if (EntryOf(item) is not { } entry)
        {
            return;
        }

        try
        {
            bool entered;
            using (var wait = NewWait($"Opening {entry.Name}"))
            {
                entered = await session.Provider.EnterAsync(entry, wait?.Token ?? CancellationToken.None);
            }

            if (entered)
            {
                await ListPluginAsync();
            }
            else if (_session == session)
            {
                PluginEntryActivated?.Invoke(this, entry);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled from the card: the panel stays where it was.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Message = PluginManager.Describe(session.Plugin, ex);
        }
    }

    /// <summary>".." and Ctrl+PgUp: up one level, or out of the plugin at its top (Far).</summary>
    private async Task PluginUpAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        var previous = session.Provider.Location;
        try
        {
            bool wentUp;
            using (var wait = NewWait("Going up"))
            {
                wentUp = await session.Provider.GoUpAsync(wait?.Token ?? CancellationToken.None);
            }

            if (wentUp)
            {
                await ListPluginAsync(LastSegment(previous));
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Message = PluginManager.Describe(session.Plugin, ex);
            return;
        }

        await LeavePluginAsync();
    }

    /// <summary>Back to the folder the panel showed before the plugin.</summary>
    internal async Task LeavePluginAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        var returnPath = session.ReturnPath;
        ClosePlugin();
        if (!await NavigateAsync(returnPath, session.ReturnFocus))
        {
            await NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
    }

    /// <summary>Drops the plugin (the provider is disposed in the background); the caller shows a folder next.</summary>
    private void ClosePlugin()
    {
        if (_session is not { } session)
        {
            return;
        }

        _session = null;
        _loading?.Cancel();
        State.SetExtension(null);
        _pluginViewHost.Content = null;
        UpdateBody();
        _ = DisposeQuietlyAsync(session.Provider);
    }

    private static async Task DisposeQuietlyAsync(IPanelProvider provider)
    {
        try
        {
            await provider.DisposeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine($"Plugin provider dispose failed: {ex}");
        }
    }

    /// <summary>The last segment of a plugin location ("s3://b/x/y/" → "y"), to keep the cursor on it after going up.</summary>
    private static string? LastSegment(string location)
    {
        var trimmed = location.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\', ':']);
        return cut >= 0 && cut < trimmed.Length - 1 ? trimmed[(cut + 1)..] : null;
    }

    /// <summary>A provider with its own WPF view (issue #78) shows it in place of the list; a failing view leaves the list.</summary>
    private void ShowPluginView(LoadedPlugin plugin, IPanelProvider provider)
    {
        _pluginViewHost.Content = null;
        if (provider is HabilisTerminal.Plugins.Wpf.IPanelView view)
        {
            try
            {
                _pluginViewHost.Content = view.CreateView(new PluginViewHost(this));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Message = PluginManager.Describe(plugin, ex);
            }
        }

        UpdateBody();
    }

    /// <summary>The host side of a plugin view: refresh and keyboard back to the panel.</summary>
    private sealed class PluginViewHost(FilePanel panel) : HabilisTerminal.Plugins.Wpf.IPanelViewHost
    {
        public void Refresh() => panel.Dispatcher.BeginInvoke(() => _ = panel.ListPluginAsync(quiet: true));

        public void FocusPanel() => panel.Dispatcher.BeginInvoke(() => panel.Focus());
    }

    /// <summary>The provider's choice of a ".." row (issue #77); a provider that fails to answer keeps it.</summary>
    private static bool ShowsParent(PluginSession session) =>
        PluginManager.Guard(session.Plugin, () => session.Provider.ShowsParent, true);

    /// <summary>The provider's name columns (issue #97); a provider that fails to answer keeps one.</summary>
    private static int NameColumns(PluginSession session) =>
        PluginManager.Guard(session.Plugin, () => session.Provider.NameColumns, 1);

    private static (string, bool) RowKey(FileEntry entry) => (entry.Name, entry.IsDirectory);

    private static FileEntry ToRow(PanelEntry entry)
    {
        var modified = entry.Modified is { } time ? (time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc)) : DateTime.MinValue;
        return new FileEntry(entry.Name, entry.IsFolder ? FileAttributes.Directory : FileAttributes.Normal, entry.Size ?? 0, modified, modified, modified);
    }

    /// <summary>One open plugin panel: the plugin, its provider, the entries by row and the panel extension.</summary>
    private sealed class PluginSession
    {
        public PluginSession(LoadedPlugin plugin, IPanelProvider provider, string returnPath, string? returnFocus)
        {
            Plugin = plugin;
            Provider = provider;
            ReturnPath = returnPath;
            ReturnFocus = returnFocus;
            Extension = new PanelExtension
            {
                Cell = (row, key) => Entries.TryGetValue(RowKey(row), out var entry) && entry.Cells is { } cells && cells.TryGetValue(key, out var cell)
                    ? (cell.Text, cell.SortValue)
                    : null,
                Group = row => Entries.TryGetValue(RowKey(row), out var entry) ? entry.Group : 0,
                Tone = row => Entries.TryGetValue(RowKey(row), out var entry) ? (int)entry.Tone : 0,
                Icon = row => Entries.TryGetValue(RowKey(row), out var entry) ? entry.Icon : null,
            };
        }

        public LoadedPlugin Plugin { get; }

        public IPanelProvider Provider { get; }

        public string ReturnPath { get; }

        public string? ReturnFocus { get; }

        public PanelExtension Extension { get; }

        public Dictionary<(string, bool), PanelEntry> Entries { get; set; } = [];

        public void ApplyLayout(int index, IPanelProvider provider)
        {
            var layouts = PluginManager.Guard(Plugin, () => provider.Layouts, []);
            var layout = layouts.Count == 0 ? null : layouts[Math.Clamp(index, 0, layouts.Count - 1)];
            Extension.Columns = layout?.Columns.Select(c => new ExtensionColumn(c.Key, c.Title, c.Width, c.AlignRight)).ToArray() ?? [];
        }
    }
}
