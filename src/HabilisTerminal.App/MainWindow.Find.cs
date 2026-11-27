using System.IO;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Search;
using HabilisTerminal.Core.Search;
using HabilisTerminal.Core.FileSystem;

namespace HabilisTerminal.App;

/// <summary>
/// Alt+F7, as in Far (issue #198): the dialog, and the results as a tab beside the consoles, as Visual Studio's Find
/// Results (issue #415). Each search has its tab; the panels under it are those of the console tab last in front.
/// </summary>
public partial class MainWindow
{
    /// <summary>Rows of the area a results tab gets at least: more than the console's usual few.</summary>
    private const int SearchRows = 18;

    private readonly List<SearchTab> _searches = [];
    private FindOptions _lastFind = FindOptions.Default;

    /// <summary>The results tab in front, or null when a console is.</summary>
    private SearchTab? _search;

    /// <summary>The console's rows while a results tab is in front; the results' own rows while a console is.</summary>
    private int _consoleRows = -1;
    private int _searchRows = -1;

    private void FindFiles()
    {
        var panel = _active;
        if (panel.IsPlugin)
        {
            panel.Message = "Alt+F7 searches folders on disk, not plugin panels.";
            return;
        }

        var selected = panel.State.SelectedFolders();
        var canSearchArchives = CanSearchArchives();
        if (FindDialog.Ask(this, _lastFind, selected.Count > 0, canSearchArchives) is not { } options)
        {
            RefocusAfterFind();
            return;
        }

        _lastFind = options;
        OpenSearchTab(StartSearch(options, panel, selected, canSearchArchives));
    }

    private bool CanSearchArchives() =>
        _plugins.Loaded.Any(p => Plugins.PluginManager.Guard(p, () => p.Instance!.FileExtensions.Any(), false));

    private FindSession StartSearch(FindOptions options, FilePanel panel, IReadOnlyList<string> selected, bool canSearchArchives)
    {
        var roots = FileSearch.RootsFor(options.Scope, panel.CurrentPath, [.. selected.Select(name => Path.Combine(panel.CurrentPath, name))]);
        var containers = options.Archives && canSearchArchives ? new PluginContainerSearch(_plugins) : null;
        return new FindSession(options, roots, App.Paths?.SearchCacheDirectory, containers);
    }

    private void RefocusAfterFind()
    {
        if (_search is { } search)
        {
            search.View.FocusList();
        }
        else
        {
            FocusActivePanel();
        }
    }

    private void OpenSearchTab(FindSession session)
    {
        var tab = new SearchTab(session);
        Layout.WorkspaceLayout.SetRole(tab.View, Layout.WorkspaceRole.Terminal);
        tab.View.IsVisible = false;
        Workspace.Children.Add(tab.View);
        tab.View.Requested += (row, request) => OnSearchRequest(tab, row, request);
        tab.View.LeaveRequested += LeaveSearch;
        tab.View.ModifyRequested += () => ModifySearch(tab);
        tab.View.PanelRequested += () => SearchToPanel(tab);
        var wasRunning = true;
        tab.View.Updated += () =>
        {
            // The strip refreshes running tabs' texts itself; the end of the search changes the mark once.
            if (wasRunning && tab.Session.IsDone)
            {
                wasRunning = false;
                UpdateTabStrip();
            }
        };
        _searches.Add(tab);
        ShowSearch(tab);
    }

    /// <summary>A results tab to the front: it takes the console's place, the panels stay.</summary>
    private void ShowSearch(SearchTab tab)
    {
        if (_search is null)
        {
            _consoleRows = Workspace.TerminalRows;
            Workspace.TerminalRows = _searchRows > 0 ? _searchRows : Math.Max(_consoleRows, SearchRows);
        }

        _search = tab;
        _tab?.Terminal.IsVisible = false;
        foreach (var search in _searches)
        {
            search.View.IsVisible = search == tab;
        }

        Workspace.InvalidateMeasure();
        UpdateTabStrip();
        UpdateHintButton();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_search == tab && !IsPageOpen)
            {
                tab.View.FocusList();
            }
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    /// <summary>Results out of the front, before a console comes back: the console gets its own height again.</summary>
    private void HideSearch()
    {
        if (_search is null)
        {
            return;
        }

        _searchRows = Workspace.TerminalRows;
        if (_consoleRows > 0)
        {
            Workspace.TerminalRows = _consoleRows;
        }

        _search = null;
        foreach (var search in _searches)
        {
            search.View.IsVisible = false;
        }
    }

    /// <summary>Esc in the results: the console and its panels; the results tab stays.</summary>
    private void LeaveSearch()
    {
        if (_tab is { } tab)
        {
            ShowTab(tab);
        }

        FocusActivePanel();
    }

    private void CloseSearch(SearchTab tab)
    {
        var index = _searches.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        var inFront = tab == _search;
        if (inFront)
        {
            HideSearch();
        }

        _searches.Remove(tab);
        Workspace.Children.Remove(tab.View);
        tab.Dispose();
        UpdateHintButton();
        if (inFront)
        {
            if (_searches.Count > 0)
            {
                ShowSearch(_searches[Math.Min(index, _searches.Count - 1)]);
            }
            else if (_tab is { } console)
            {
                ShowTab(console);
            }
        }

        UpdateTabStrip();
    }

    private void OnSearchRequest(SearchTab tab, Core.Search.FindRow row, FindRequest request)
    {
        if (row.Item is not { } item)
        {
            return;
        }

        var panel = _active;
        var isFile = !item.IsFolder && row.Kind != Core.Search.FindRowKind.Folder;
        if (!isFile || (request == FindRequest.Open && row.Line is null))
        {
            // A file or folder goes to its panel: the console comes back with the panel at it.
            LeaveSearchFor(GoToFoundAsync(panel, item));
            return;
        }

        if (item.Container is not null)
        {
            // An entry inside an archive: a copy to look at, as it cannot be saved back.
            Fire(ViewArchiveEntryAsync(panel, item));
            return;
        }

        if (request == FindRequest.Edit)
        {
            Edit(item.Path, panel, followFile: false, at: row.Line);
        }
        else
        {
            OpenPlainViewer(item.Path, panel, row.Line);
        }
    }

    private void LeaveSearchFor(Task work)
    {
        if (_tab is { } console)
        {
            ShowTab(console);
        }

        Fire(work);
    }

    /// <summary>Modify find: the dialog with this tab's options; the new search replaces the results in the tab.</summary>
    private void ModifySearch(SearchTab tab)
    {
        var panel = _active;
        var selected = panel.State.SelectedFolders();
        var canSearchArchives = CanSearchArchives();
        if (FindDialog.Ask(this, tab.Options, selected.Count > 0, canSearchArchives) is not { } options)
        {
            tab.View.FocusList();
            return;
        }

        _lastFind = options;
        tab.Replace(StartSearch(options, panel, selected, canSearchArchives));
        UpdateTabStrip();
        tab.View.FocusList();
    }

    /// <summary>Panel: the files found as a temporary panel, the search stopped where it is.</summary>
    private void SearchToPanel(SearchTab tab)
    {
        tab.Session.Stop();
        LeaveSearchFor(ShowFoundInPanelAsync(_active, tab.Session));
    }

    /// <summary>Called when a page closes: a viewer or editor opened from the results returns to them.</summary>
    private void ReturnToFindResults()
    {
        if (_search is { } search)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(search.View.FocusList, Avalonia.Threading.DispatcherPriority.Input);
        }
    }

    private async Task GoToFoundAsync(FilePanel panel, FoundItem item)
    {
        if (item is { Container: { } container, InnerPath: { } inner })
        {
            // Inside an archive: its panel at the entry's folder, with the cursor on the entry.
            var innerFolder = Path.GetDirectoryName(inner) ?? string.Empty;
            var location = innerFolder.Length == 0 ? container : container + "\\" + innerFolder;
            if (await OpenLocationInFileAsync(panel, location))
            {
                panel.State.TrySetCursor(Path.GetFileName(inner));
                panel.Refresh();
            }

            panel.Focus();
            return;
        }

        var folder = Path.GetDirectoryName(item.Path.TrimEndSeparator()) ?? item.Path;
        await panel.NavigateAsync(folder, Path.GetFileName(item.Path.TrimEndSeparator()));
        panel.Focus();
    }

    /// <summary>
    /// An entry found inside an archive, copied to a temporary file and shown in the viewer; closing it returns to the
    /// results. The plugin is asked for the archive's entries again, and the copy is made from the one found.
    /// </summary>
    private async Task ViewArchiveEntryAsync(FilePanel panel, FoundItem item)
    {
        using var containers = new PluginContainerSearch(_plugins);
        string? copy = null;
        try
        {
            copy = await Task.Run(async () =>
            {
                if (containers.Enumerate(item.Container!, CancellationToken.None) is not { } entries)
                {
                    return null;
                }

                await foreach (var entry in entries)
                {
                    if (entry.OpenAsync is { } open && string.Equals(entry.InnerPath.Replace('/', '\\').Trim('\\'), item.InnerPath, StringComparison.OrdinalIgnoreCase))
                    {
                        var found = Path.Combine(Path.GetTempPath(), Core.Settings.AppPaths.ProductFolder, "found");
                        DeleteOldCopies(found);
                        var folder = Path.Combine(found, Path.GetRandomFileName());
                        Directory.CreateDirectory(folder);
                        var target = Path.Combine(folder, item.InnerPath!.Split('\\')[^1]);
                        await using (var source = await open(CancellationToken.None))
                        await using (var file = File.Create(target))
                        {
                            await source.CopyToAsync(file);
                        }

                        return target;
                    }
                }

                return null;
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            panel.Message = $"Could not read '{item.InnerPath}': {ex.Message}";
        }

        if (copy is null)
        {
            panel.Message ??= $"'{item.InnerPath}' is no longer in the archive.";
            return;
        }

        OpenViewer(copy, panel);
    }

    /// <summary>Copies viewed from archives an hour ago or more: their viewers are long closed.</summary>
    private static void DeleteOldCopies(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var old in new DirectoryInfo(folder).GetDirectories().Where(d => d.CreationTimeUtc < DateTime.UtcNow.AddHours(-1)))
        {
            try
            {
                old.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still open somewhere: the next time.
            }
        }
    }

    /// <summary>
    /// The results as a temporary panel: names carry their folders below the search root, so F5, F6 and F8 work on the
    /// real files. Entries inside archives are not real files and stay out; the panel says how many.
    /// </summary>
    private static async Task ShowFoundInPanelAsync(FilePanel panel, FindSession session)
    {
        await session.Task;
        var all = session.Since(0);
        var items = all.Where(i => i.Container is null).ToList();
        var root = session.CommonRoot ?? Path.GetPathRoot(items.Count > 0 ? items[0].Path : panel.CurrentPath) ?? panel.CurrentPath;
        var names = items.Select(i => i.Path.StartsWith(root.WithEndSeparator(), StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(root, i.Path) : i.Path).ToList();
        await panel.ShowFoundAsync(root, $"Found: {session.Options.Masks}", names);
        if (all.Count > items.Count)
        {
            panel.Message = $"{all.Count - items.Count:N0} found inside archives are not in this panel: use Go to from the results.";
        }

        panel.Focus();
    }
}
