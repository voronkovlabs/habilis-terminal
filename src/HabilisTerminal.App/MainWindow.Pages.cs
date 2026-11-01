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

    /// <summary>Files larger than this ask before opening in the editor (SPEC §9).</summary>
    private const long LargeFileBytes = 50L * 1024 * 1024;

    /// <summary>F4 from the panel.</summary>
    private void Edit(FilePanel panel)
    {
        var item = panel.State.Current;
        if (item.IsParent || item.Entry.IsDirectory || panel.State.Count == 0)
        {
            return;
        }

        Edit(Path.Combine(panel.CurrentPath, item.Name), panel);
    }

    /// <summary>
    /// Text files open in the built-in editor; binary files go to the system editor (shell "edit", else "open");
    /// very large text files ask first (SPEC §9).
    /// </summary>
    private void Edit(string path, FilePanel returnTo)
    {
        try
        {
            var sample = new byte[64 * 1024];
            int read;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                read = stream.Read(sample, 0, sample.Length);
            }

            if (!TextEncoding.LooksLikeText(sample.AsSpan(0, read)))
            {
                OpenWithSystemEditor(path, returnTo);
                return;
            }

            if (new FileInfo(path).Length > LargeFileBytes)
            {
                var body = new System.Windows.Controls.TextBlock { Text = $"'{Path.GetFileName(path)}' is large ({Panels.PanelColumns.FormatCompactSize(new FileInfo(path).Length, System.Globalization.CultureInfo.CurrentCulture)}). Editing loads it into memory." };
                body.SetResourceReference(StyleProperty, "Habi.LabelStyle");
                var choice = Widgets.ThemedDialog.Show(this, "Large file", body,
                    [new Widgets.DialogButton<int>("Open in _editor", 1), new Widgets.DialogButton<int>("Open in _viewer", 2, IsDefault: true), new Widgets.DialogButton<int>("Cancel", 0, IsCancel: true)], 0);
                if (choice == 2)
                {
                    OpenViewer(path, returnTo);
                }

                if (choice != 1)
                {
                    return;
                }
            }

            var page = new Editing.EditorPage(this, Core.Editing.TextFile.Load(path), _themes.Current);
            page.CloseRequested += () => ReturnToPanel(returnTo, path);
            page.ShowInPanelRequested += file => ReturnToPanel(returnTo, file);
            page.ViewRequested += file => OpenViewer(file, returnTo);
            ShowPage(page);
            Dispatcher.BeginInvoke(() => page.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Widgets.OperationDialogs.Message(this, "Edit", ex.Message);
        }
    }

    private void ReturnToPanel(FilePanel panel, string file)
    {
        ClosePage();
        Fire(ReturnToPanelAsync(panel, file));
    }

    private static async Task ReturnToPanelAsync(FilePanel panel, string file)
    {
        var directory = Path.GetDirectoryName(file)!;
        if (!string.Equals(directory, panel.CurrentPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            await panel.NavigateAsync(directory, Path.GetFileName(file));
        }
        else
        {
            await panel.ReloadAsync();
            panel.State.TrySetCursor(Path.GetFileName(file));
            panel.Refresh();
        }
    }

    private static void OpenWithSystemEditor(string path, FilePanel panel)
    {
        foreach (var verb in new[] { "edit", "open" })
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true, Verb = verb });
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No handler for this verb: try the next one.
            }
        }

        panel.Message = $"No program is associated with '{Path.GetFileName(path)}'.";
    }
}
