using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Viewing;
using HabilisTerminal.Core.Panels;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.App;

/// <summary>Full-window pages over panels and terminal: viewer (F3), editor (F4), settings (SPEC §9, §14).</summary>
public partial class MainWindow
{
    private bool IsPageOpen => PageHost.Content is not null;

    private void ShowPage(Control page)
    {
        using var measure = Core.Diagnostics.Perf.Measure("page.show", page.GetType().Name);
        ClosePage(refocus: false);
        Workspace.Visibility = Visibility.Hidden;
        PageHost.Content = page;
        PageHost.IsVisible = true;
        UpdateHintButton();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (page is IPage content)
            {
                content.FocusContent();
            }
            else
            {
                page.Focus();
            }

            UpdatePanelKeys();
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    private void ClosePage(bool refocus = true)
    {
        if (PageHost.Content is IDisposable disposable)
        {
            disposable.Dispose();
        }

        PageHost.Content = null;
        PageHost.IsVisible = false;
        Workspace.IsVisible = true;
        UpdateHintButton();
        if (refocus)
        {
            FocusActivePanel();
            ReturnToFindResults();
        }
    }

    /// <summary>
    /// F3: files open in the viewer as their content, whatever the format (issue #231). On a folder its files are counted and sized in the background; with folders
    /// selected all of them are counted (Far).
    /// </summary>
    private void View(FilePanel panel)
    {
        var selected = panel.State.SelectedFolders();
        if (selected.Count > 0)
        {
            Fire(panel.CountFoldersAsync(selected));
            return;
        }

        var item = panel.State.Current;
        if (item.IsParent || panel.State.Count == 0)
        {
            return;
        }

        var path = Path.Combine(panel.CurrentPath, item.Name);
        if (item.Entry.IsLink && item.Entry.IsDirectory)
        {
            panel.Message = $"{Core.FileSystem.LinkInfo.Name(item.Entry.Link)}: not counted. Ctrl+A shows where it leads.";
            return;
        }

        if (item.Entry.IsDirectory)
        {
            Fire(panel.CountFoldersAsync([item.Name]));
            return;
        }

        OpenViewer(path, returnTo: panel);
    }

    /// <summary>F3 and everything that views a file: the text viewer, with hex on F4.</summary>
    private void OpenViewer(string path, FilePanel returnTo) => OpenPlainViewer(path, returnTo);

    /// <summary>
    /// Alt+F3 inside the program: PDF pages, pictures. False for any other file, which goes to its associated program.
    /// Markdown went to a WebView2 page, which the Avalonia version has not yet (issue #253): it opens in its program.
    /// </summary>
    private bool OpenRich(string path, FilePanel returnTo)
    {
        if (PdfImagePage.IsPdf(path))
        {
            OpenPdf(path, returnTo);
        }
        else if (ImagePage.IsImage(path))
        {
            OpenImage(path, returnTo);
        }
        else
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// The pictures of the panel's folder in its order (issue #242), so Left / Right step through them; a copy from a
    /// plugin, or a file elsewhere, is shown alone. Closed, the cursor stands on the last picture shown, as in Far.
    /// </summary>
    private void OpenImage(string path, FilePanel returnTo)
    {
        List<string> files = [path];
        var folder = Path.GetDirectoryName(path)?.TrimEnd('\\');
        if (!returnTo.IsPlugin && string.Equals(folder, returnTo.CurrentPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            var pictures = returnTo.State.Entries.Where(e => !e.IsDirectory && ImagePage.IsImage(e.Name)).Select(e => Path.Combine(returnTo.CurrentPath, e.Name)).ToList();
            if (pictures.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            {
                files = pictures;
            }
        }

        var page = new ImagePage(files, files.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
        RememberFile(path, edited: false);
        page.CloseRequested += () =>
        {
            ClosePage();
            returnTo.State.TrySetCursor(Path.GetFileName(page.FilePath));
            returnTo.Refresh();
        };
        page.ViewRequested += file => OpenViewer(file, returnTo);
        ShowPage(page);
    }

    private void OpenPdf(string path, FilePanel returnTo)
    {
        void Close()
        {
            ClosePage();
            returnTo.State.TrySetCursor(Path.GetFileName(path));
            returnTo.Refresh();
        }

        RememberFile(path, edited: false);
        var pages = new PdfImagePage(path);
        pages.CloseRequested += Close;
        pages.ViewRequested += () => OpenViewer(path, returnTo);
        // Searching went to the Edge PDF viewer in a WebView2 page; for now the PDF opens in its program (issue #253).
        pages.SearchRequested += _ => OpenWithAssociation(returnTo, path, Path.GetDirectoryName(path) ?? returnTo.CurrentPath);
        ShowPage(pages);
    }

    private void OpenPlainViewer(string path, FilePanel returnTo)
    {
        var page = new ViewerPage(path, _settings, _themes.Current);
        RememberFile(path, edited: false);
        page.CloseRequested += () =>
        {
            ClosePage();
            returnTo.State.TrySetCursor(Path.GetFileName(path));
            returnTo.Refresh();
        };
        page.EditRequested += file => Edit(file, returnTo);
        ShowPage(page);
    }

    /// <summary>
    /// Alt+F3 (issues #57, #231): Markdown, PDF and pictures in the program (<see cref="OpenRich"/>); any other file in
    /// its associated program, as Far's external viewer.
    /// </summary>
    private void ViewAlternative(FilePanel panel)
    {
        var item = panel.State.Current;
        if (panel.IsPlugin || item.IsParent || item.Entry.IsDirectory || panel.State.Count == 0)
        {
            return;
        }

        if (!OpenRich(Path.Combine(panel.CurrentPath, item.Name), panel))
        {
            OpenWithAssociation(panel, item.Name);
        }
    }

    /// <summary>Ctrl+Q: quick view of the active panel's current file in the other panel.</summary>
    private void ToggleQuickView()
    {
        // Pressed in the panel showing it: this panel goes back to its list, as in Far (issue #128).
        if (_active.QuickViewSource is not null)
        {
            var panel = _active;
            panel.QuickViewSource = null;
            RevealOtherContent(panel, on: false);
            return;
        }

        var other = Other(_active);
        if (!HiddenPanelReveal.TurnsOn(IsShown(other), other.QuickViewSource is not null))
        {
            other.QuickViewSource = null;
            RevealOtherContent(other, on: false);
            return;
        }

        other.InfoSource = null;
        other.QuickViewSource = _active;
        RevealOtherContent(other, on: true);
    }

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

    /// <summary>Shift+F4, as in Far: asks for a name and edits that file, which is created by the first save (issue #93).</summary>
    private void NewFile(FilePanel panel)
    {
        var name = Widgets.OperationDialogs.NewFile(this);
        panel.Focus();
        if (name is null)
        {
            return;
        }

        var invalid = Path.GetInvalidFileNameChars();
        if (Path.GetFileName(name) is not { Length: > 0 } fileName || fileName.IndexOfAny(invalid) >= 0 || name.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            Widgets.OperationDialogs.Message(this, "New file", $"'{name}' is not a valid file name.");
            panel.Focus();
            return;
        }

        var path = Path.GetFullPath(Path.Combine(panel.CurrentPath, name));
        if (Directory.Exists(path))
        {
            Widgets.OperationDialogs.Message(this, "New file", $"'{name}' is a folder.");
            panel.Focus();
        }
        else if (File.Exists(path))
        {
            Edit(path, panel);
        }
        else
        {
            OpenEditor(Core.Editing.TextFile.New(path), panel, followFile: true);
        }
    }

    /// <summary>
    /// Text files open in the built-in editor; binary files go to the system editor (shell "edit", else "open");
    /// very large text files ask first (SPEC §9).
    /// </summary>
    /// <param name="followFile">Esc puts the panel cursor on the file (moving the panel to its folder); off for settings.json.</param>
    private void Edit(string path, FilePanel returnTo, bool followFile = true)
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

            var large = new FileInfo(path).Length > _settings.EditorLargeFileThresholdMB * 1024L * 1024;
            var hugeLine = !large && Core.Editing.LongLines.HasHugeLine(path);
            if (large || hugeLine)
            {
                var body = new Avalonia.Controls.TextBlock
                {
                    Text = large
                        ? $"'{Path.GetFileName(path)}' is large ({Panels.PanelColumns.FormatCompactSize(new FileInfo(path).Length, System.Globalization.CultureInfo.CurrentCulture)}). Editing loads it into memory."
                        : $"'{Path.GetFileName(path)}' has a line of over two million characters. The editor may stop responding for minutes.",
                };
                body.Classes.Add("label");
                var choice = Widgets.ThemedDialog.Show(this, large ? "Large file" : "Very long line", body,
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

            OpenEditor(Core.Editing.TextFile.Load(path), returnTo, followFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Widgets.OperationDialogs.Message(this, "Edit", ex.Message);
        }
    }

    private void OpenEditor(Core.Editing.TextFile file, FilePanel returnTo, bool followFile)
    {
        var path = file.Path;
        if (File.Exists(path))
        {
            RememberFile(path, edited: true);
        }

        var page = new Editing.EditorPage(this, file, _themes.Current, _settings);
        page.CloseRequested += () =>
        {
            // A new file that was never saved has nothing to follow, and its folder may not exist.
            if (followFile && File.Exists(path))
            {
                RememberFile(path, edited: true);
                ReturnToPanel(returnTo, path);
            }
            else
            {
                ClosePage();
            }
        };
        page.ShowInPanelRequested += saved => ReturnToPanel(returnTo, saved);
        page.ViewRequested += saved => OpenViewer(saved, returnTo);
        ShowPage(page);
        Avalonia.Threading.Dispatcher.UIThread.Post(page.FocusContent, Avalonia.Threading.DispatcherPriority.Input);
    }

    private void ReturnToPanel(FilePanel panel, string file)
    {
        ClosePage();
        Fire(ReturnToPanelAsync(panel, file));
    }

    private static async Task ReturnToPanelAsync(FilePanel panel, string file)
    {
        var directory = Path.GetDirectoryName(file)!;
        FilePanel.InvalidateFolderSizes(file); // the editor may have changed the file's size
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
