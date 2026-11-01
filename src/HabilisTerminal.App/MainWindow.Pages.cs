using System.IO;
using System.Windows;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Viewing;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.App;

/// <summary>Full-window pages over panels and terminal: viewer (F3), editor (F4), settings (SPEC §9, §14).</summary>
public partial class MainWindow
{
    private bool IsPageOpen => PageHost.Content is not null;

    private void ShowPage(FrameworkElement page)
    {
        ClosePage(refocus: false);
        Workspace.Visibility = Visibility.Hidden;
        PageHost.Content = page;
        PageHost.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            SetFocus(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            page.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ClosePage(bool refocus = true)
    {
        if (PageHost.Content is IDisposable disposable)
        {
            disposable.Dispose();
        }

        PageHost.Content = null;
        PageHost.Visibility = Visibility.Collapsed;
        Workspace.Visibility = Visibility.Visible;
        if (refocus)
        {
            FocusActivePanel();
        }
    }

    /// <summary>F3: files open in the viewer; on a folder the size is computed in the background (Far).</summary>
    private void View(FilePanel panel)
    {
        var item = panel.State.Current;
        if (item.IsParent || panel.State.Count == 0)
        {
            return;
        }

        var path = Path.Combine(panel.CurrentPath, item.Name);
        if (item.Entry.IsDirectory)
        {
            Fire(ComputeFolderSizeAsync(panel, item.Name, path));
            return;
        }

        OpenViewer(path, returnTo: panel);
    }

    private void OpenViewer(string path, FilePanel returnTo)
    {
        var page = new ViewerPage(path);
        page.CloseRequested += () =>
        {
            ClosePage();
            returnTo.State.TrySetCursor(Path.GetFileName(path));
            returnTo.Refresh();
        };
        page.EditRequested += file => Edit(file, returnTo);
        ShowPage(page);
    }

    private static async Task ComputeFolderSizeAsync(FilePanel panel, string name, string path)
    {
        var result = await Task.Run(() => DirectorySize.Compute(path));
        panel.State.FolderSizes[name] = result.Bytes;
        panel.State.MoveCursor(1);
        panel.Refresh();
        if (result.Inaccessible > 0)
        {
            panel.Message = $"{result.Inaccessible} folder(s) inside '{name}' could not be read; the size is partial.";
        }
    }

    /// <summary>Ctrl+Q: quick view of the active panel's current file in the other panel.</summary>
    private void ToggleQuickView()
    {
        var other = Other(_active);
        if (other.QuickViewSource is not null)
        {
            other.QuickViewSource = null;
            return;
        }

        other.InfoSource = null;
        other.QuickViewSource = _active;
    }

    /// <summary>F4 is implemented in stage 9.</summary>
    private static void Edit(string path, FilePanel returnTo) =>
        returnTo.Message = $"Editing '{Path.GetFileName(path)}' arrives with the editor.";
}
