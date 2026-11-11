using System.IO;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Search;
using HabilisTerminal.Core.Search;

namespace HabilisTerminal.App;

/// <summary>Alt+F7, as in Far (issue #198): the dialog, the results, and the temporary panel.</summary>
public partial class MainWindow
{
    private FindOptions _lastFind = FindOptions.Default;
    private FindSession? _find;

    /// <summary>Set while a viewer or editor opened from the results is up: closing it brings the results back.</summary>
    private bool _returnToFind;

    private void FindFiles()
    {
        var panel = _active;
        if (panel.IsPlugin)
        {
            panel.Message = "Alt+F7 searches folders on disk, not plugin panels.";
            return;
        }

        var selected = panel.State.SelectedFolders();
        var canSearchArchives = _plugins.Loaded.Any(p => Plugins.PluginManager.Guard(p, () => p.Instance!.FileExtensions.Any(), false));
        if (FindDialog.Ask(this, _lastFind, selected.Count > 0, canSearchArchives) is not { } options)
        {
            FocusActivePanel();
            return;
        }

        _lastFind = options;
        _find?.Dispose();
        var roots = FileSearch.RootsFor(options.Scope, panel.CurrentPath, [.. selected.Select(name => Path.Combine(panel.CurrentPath, name))]);
        var containers = options.Archives && canSearchArchives ? new PluginContainerSearch(_plugins) : null;
        _find = new FindSession(options, roots, App.Paths?.SearchCacheDirectory, containers);
        ShowFindResults();
    }

    private void ShowFindResults()
    {
        if (_find is not { } session)
        {
            return;
        }

        var (action, item) = FindResultsWindow.Show(this, session);
        var panel = _active;
        switch (action)
        {
            case FindAction.GoTo when item is not null:
                session.Stop();
                Fire(GoToFoundAsync(panel, item));
                break;
            case FindAction.View or FindAction.Edit when item is { IsFolder: false, Container: not null }:
                // An entry inside an archive: a copy to look at, as it cannot be saved back.
                Fire(ViewArchiveEntryAsync(panel, item));
                break;
            case FindAction.View when item is { IsFolder: false }:
                _returnToFind = true;
                OpenViewer(item.Path, panel);
                break;
            case FindAction.Edit when item is { IsFolder: false }:
                _returnToFind = true;
                Edit(item.Path, panel);

                // A binary file goes to the system editor, and a large one may be refused: no page, nothing to return from.
                if (!IsPageOpen)
                {
                    _returnToFind = false;
                    ShowFindResults();
                }

                break;
            case FindAction.View or FindAction.Edit when item is not null:
                session.Stop();
                Fire(GoToFoundAsync(panel, item));
                break;
            case FindAction.Panel:
                session.Stop();
                Fire(ShowFoundInPanelAsync(panel, session));
                break;
            case FindAction.NewSearch:
                session.Stop();
                FindFiles();
                break;
            default:
                session.Stop();
                FocusActivePanel();
                break;
        }
    }

    /// <summary>Called when a page closes: a viewer or editor opened from the results returns to them.</summary>
    private void ReturnToFindResults()
    {
        if (_returnToFind)
        {
            _returnToFind = false;
            Avalonia.Threading.Dispatcher.UIThread.Post(ShowFindResults, Avalonia.Threading.DispatcherPriority.Background);
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

        var folder = Path.GetDirectoryName(item.Path.TrimEnd('\\')) ?? item.Path;
        await panel.NavigateAsync(folder, Path.GetFileName(item.Path.TrimEnd('\\')));
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
                        var target = Path.Combine(folder, Path.GetFileName(item.InnerPath!));
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
            ShowFindResults();
            return;
        }

        _returnToFind = true;
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
        var names = items.Select(i => i.Path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(root, i.Path) : i.Path).ToList();
        await panel.ShowFoundAsync(root, $"Found: {session.Options.Masks}", names);
        if (all.Count > items.Count)
        {
            panel.Message = $"{all.Count - items.Count:N0} found inside archives are not in this panel: use Go to from the results.";
        }

        panel.Focus();
    }
}
