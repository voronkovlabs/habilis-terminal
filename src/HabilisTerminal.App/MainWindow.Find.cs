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
        if (FindDialog.Ask(this, _lastFind, selected.Count > 0) is not { } options)
        {
            FocusActivePanel();
            return;
        }

        _lastFind = options;
        _find?.Dispose();
        var roots = FileSearch.RootsFor(options.Scope, panel.CurrentPath, [.. selected.Select(name => Path.Combine(panel.CurrentPath, name))]);
        _find = new FindSession(options, roots);
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
            Dispatcher.BeginInvoke(ShowFindResults, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private static async Task GoToFoundAsync(FilePanel panel, FoundItem item)
    {
        var folder = Path.GetDirectoryName(item.Path.TrimEnd('\\')) ?? item.Path;
        await panel.NavigateAsync(folder, Path.GetFileName(item.Path.TrimEnd('\\')));
        panel.Focus();
    }

    /// <summary>The results as a temporary panel: names carry their folders below the search root, so F5, F6 and F8 work on the real files.</summary>
    private static async Task ShowFoundInPanelAsync(FilePanel panel, FindSession session)
    {
        await session.Task;
        var items = session.Since(0);
        var root = session.CommonRoot ?? Path.GetPathRoot(items.Count > 0 ? items[0].Path : panel.CurrentPath) ?? panel.CurrentPath;
        var names = items.Select(i => i.Path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(root, i.Path) : i.Path).ToList();
        await panel.ShowFoundAsync(root, $"Found: {session.Options.Masks}", names);
        panel.Focus();
    }
}
