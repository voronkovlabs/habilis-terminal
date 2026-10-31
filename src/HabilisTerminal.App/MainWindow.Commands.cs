using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using HabilisTerminal.App.Hosting;
using HabilisTerminal.App.Input;
using HabilisTerminal.App.Layout;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Drives;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Shell.ConPty;

namespace HabilisTerminal.App;

/// <summary>Panel commands of the window (SPEC §5).</summary>
public partial class MainWindow
{
    /// <summary>Keys intercepted before they reach either panels or terminal (SPEC §4, §5).</summary>
    private bool OnGlobalKey(Key key, KeyModifiers modifiers)
    {
        switch (key)
        {
            case Key.O when modifiers == KeyModifiers.Control:
                TogglePanels();
                return true;

            case Key.Tab when modifiers == KeyModifiers.Control && AnyPanelVisible:
                if (PanelHasFocus)
                {
                    FocusTerminal();
                }
                else
                {
                    FocusActivePanel();
                }

                return true;

            case Key.F4 when modifiers == KeyModifiers.Alt:
                Close();
                return true;
        }

        if (FocusedPanel() is not { } panel)
        {
            return false;
        }

        // Quick search sees its keys first (Backspace, Esc, Ctrl+Enter...).
        if (panel.IsQuickSearchActive && panel.HandleKey(key, modifiers))
        {
            return true;
        }

        if (HandleWindowKey(panel, key, modifiers))
        {
            return true;
        }

        return panel.HandleKey(key, modifiers);
    }

    private bool HandleWindowKey(FilePanel panel, Key key, KeyModifiers modifiers)
    {
        switch (key)
        {
            case Key.Tab when modifiers == KeyModifiers.None:
                if (IsShown(Other(panel)))
                {
                    Activate(Other(panel));
                    FocusActivePanel();
                    SyncShellDirectory();
                }

                return true;

            case Key.F1 when modifiers == KeyModifiers.Control:
                SetPanelVisibility(!Workspace.LeftVisible, Workspace.RightVisible);
                return true;

            case Key.F2 when modifiers == KeyModifiers.Control:
                SetPanelVisibility(Workspace.LeftVisible, !Workspace.RightVisible);
                return true;

            case Key.F1 when modifiers == KeyModifiers.Alt:
                _ = ShowDriveMenuAsync(_left);
                return true;

            case Key.F2 when modifiers == KeyModifiers.Alt:
                _ = ShowDriveMenuAsync(_right);
                return true;

            case Key.F12 when modifiers == KeyModifiers.Alt:
                _ = ShowFolderHistoryAsync();
                return true;

            case Key.F12 when modifiers == KeyModifiers.Control:
                _ = ShowSortMenuAsync(panel);
                return true;

            case Key.U when modifiers == KeyModifiers.Control:
                SwapPanels();
                return true;

            case Key.L when modifiers == KeyModifiers.Control:
                ToggleInfoPanel();
                return true;

            case Key.Add when modifiers == KeyModifiers.None:
                _ = SelectByMaskAsync(panel, select: true);
                return true;

            case Key.Subtract when modifiers == KeyModifiers.None:
                _ = SelectByMaskAsync(panel, select: false);
                return true;

            case Key.F10 when modifiers == KeyModifiers.None:
                Close();
                return true;

            // Command line from panel focus, as in Far.
            case Key.Enter when modifiers == KeyModifiers.Control:
                InsertIntoCommandLine(ShellQuote.Quote(panel.State.Current.Name));
                return true;

            case Key.OemOpenBrackets when modifiers == KeyModifiers.Control:
                InsertIntoCommandLine(ShellQuote.Quote(_left.CurrentPath));
                return true;

            case Key.Oem6 when modifiers == KeyModifiers.Control: // Ctrl+]
                InsertIntoCommandLine(ShellQuote.Quote(_right.CurrentPath));
                return true;

            case Key.F when modifiers == KeyModifiers.Control:
                InsertIntoCommandLine(ShellQuote.Quote(FullPath(panel, panel.State.Current.Name)));
                return true;

            case Key.E when modifiers == KeyModifiers.Control:
                _shell?.Host.SendKey(Win32InputSequence.VkUp, controlKeyState: Win32InputSequence.EnhancedKey);
                return true;

            case Key.X when modifiers == KeyModifiers.Control:
                _shell?.Host.SendKey(Win32InputSequence.VkDown, controlKeyState: Win32InputSequence.EnhancedKey);
                return true;

            case Key.Space when modifiers == KeyModifiers.Control:
                _shell?.Host.SendKey(Win32InputSequence.VkSpace, ' ', Win32InputSequence.LeftCtrl);
                return true;

            // Clipboard.
            case Key.Insert when modifiers == KeyModifiers.Control:
                CopyNames(panel, fullPaths: false);
                return true;

            case Key.Insert when modifiers == (KeyModifiers.Control | KeyModifiers.Alt):
            case Key.Insert when modifiers == (KeyModifiers.Alt | KeyModifiers.Shift):
                CopyNames(panel, fullPaths: true);
                return true;

            // Layout.
            case Key.Up when modifiers == KeyModifiers.Control:
                Workspace.TerminalRows++;
                return true;

            case Key.Down when modifiers == KeyModifiers.Control:
                Workspace.TerminalRows = Math.Max(WorkspaceLayout.MinTerminalRows, Workspace.TerminalRows - 1);
                return true;

            case Key.Left when modifiers == KeyModifiers.Control:
            case Key.Right when modifiers == KeyModifiers.Control:
                MoveSplitter(key == Key.Left ? -1 : 1);
                return true;
        }

        return false;
    }

    private static string FullPath(FilePanel panel, string name) =>
        name == ".." ? panel.CurrentPath : Path.Combine(panel.CurrentPath, name);

    private void InsertIntoCommandLine(string text) => _shell?.Host.Insert(text + " ");

    private static void CopyNames(FilePanel panel, bool fullPaths)
    {
        var names = panel.State.TargetEntries().Select(e => fullPaths ? FullPath(panel, e.Name) : e.Name).ToArray();
        if (names.Length == 0)
        {
            names = [fullPaths ? panel.CurrentPath : panel.State.Current.Name];
        }

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, names));
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            panel.Message = $"Clipboard is busy: {ex.Message}";
        }
    }

    private void MoveSplitter(int direction)
    {
        var step = Workspace.CellSize.Width / Math.Max(1, Workspace.ActualWidth);
        if (Workspace.LeftVisible && Workspace.RightVisible)
        {
            Workspace.SplitRatio = Math.Clamp(Workspace.SplitRatio + (direction * step), 0.1, 0.9);
        }
        else
        {
            // One panel: Ctrl+Right widens the left panel, Ctrl+Left widens the right one.
            var sign = Workspace.LeftVisible ? direction : -direction;
            Workspace.SinglePanelRatio = Math.Clamp(Workspace.SinglePanelRatio + (sign * step), 0.1, 0.9);
        }
    }

    private void SwapPanels()
    {
        (_left, _right) = (_right, _left);
        WorkspaceLayout.SetRole(_left, WorkspaceRole.LeftPanel);
        WorkspaceLayout.SetRole(_right, WorkspaceRole.RightPanel);
        Workspace.InvalidateArrange();
        if (!IsShown(_active))
        {
            Activate(Other(_active));
            SyncShellDirectory();
        }

        FocusActivePanel();
    }

    private void ToggleInfoPanel()
    {
        var other = Other(_active);
        other.InfoSource = other.InfoSource is null ? _active : null;
        _active.InfoSource = null;
    }

    private static async Task SelectByMaskAsync(FilePanel panel, bool select)
    {
        var result = await QuickPick.ShowAsync(panel, [], new QuickPickOptions
        {
            Title = select ? "Select files" : "Deselect files",
            Placeholder = "Mask, e.g. *.cs,*.xaml|*.g.cs",
            InitialText = "*.*",
            AllowFreeText = true,
        });

        panel.Focus();
        if (result is null || string.IsNullOrWhiteSpace(result.Text))
        {
            return;
        }

        if (!FileMask.TryParse(result.Text, out var mask) || mask is null)
        {
            panel.Message = $"Invalid mask: {result.Text}";
            return;
        }

        if (select)
        {
            panel.State.Selection.SelectByMask(panel.State.Entries, mask);
        }
        else
        {
            panel.State.Selection.DeselectByMask(panel.State.Entries, mask);
        }

        panel.Refresh();
    }

    private static async Task ShowSortMenuAsync(FilePanel panel)
    {
        var sort = panel.State.Sort;
        (string Label, string Keys, Core.FileSystem.SortMode Mode)[] modes =
        [
            ("Name", "Ctrl+F3", Core.FileSystem.SortMode.Name),
            ("Extension", "Ctrl+F4", Core.FileSystem.SortMode.Extension),
            ("Modified", "Ctrl+F5", Core.FileSystem.SortMode.Modified),
            ("Size", "Ctrl+F6", Core.FileSystem.SortMode.Size),
            ("Unsorted", "Ctrl+F7", Core.FileSystem.SortMode.Unsorted),
            ("Created", "Ctrl+F8", Core.FileSystem.SortMode.Created),
            ("Accessed", "Ctrl+F9", Core.FileSystem.SortMode.Accessed),
        ];

        var items = modes.Select(m => new QuickPickItem
        {
            Label = m.Label,
            Icon = m.Mode == sort.Mode ? (sort.Descending ? "" : "") : string.Empty,
            Detail = m.Keys,
            Tag = m.Mode,
            IsCurrent = m.Mode == sort.Mode,
        }).ToList();
        items.Add(new QuickPickItem { Label = "Directories first", Group = "Options", Icon = sort.DirectoriesFirst ? "" : string.Empty, Tag = "dirs" });
        items.Add(new QuickPickItem { Label = "Selected first", Icon = panel.State.SelectedFirst ? "" : string.Empty, Detail = "Shift+F12", Tag = "selected" });

        var result = await QuickPick.ShowAsync(panel, items, new QuickPickOptions { Title = "Sort by", Placeholder = "Sort mode" });
        panel.Focus();
        switch (result?.Item?.Tag)
        {
            case Core.FileSystem.SortMode mode:
                panel.State.ApplySortKey(mode);
                break;
            case "dirs":
                panel.State.SetSort(sort with { DirectoriesFirst = !sort.DirectoriesFirst });
                break;
            case "selected":
                panel.State.SetSelectedFirst(!panel.State.SelectedFirst);
                break;
            default:
                return;
        }

        panel.Refresh();
    }

    private async Task ShowFolderHistoryAsync()
    {
        var panel = _active;
        var items = _history.RecentFirst.Select(path => new QuickPickItem
        {
            Label = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path,
            Description = path,
            Icon = "",
            Tag = path,
            IsCurrent = string.Equals(path, panel.CurrentPath, StringComparison.OrdinalIgnoreCase),
        }).ToList();

        var result = await QuickPick.ShowAsync(panel, items, new QuickPickOptions { Title = "Folder history", Placeholder = "Filter folders" });
        panel.Focus();
        if (result?.Item?.Tag is string target)
        {
            await panel.NavigateAsync(target);
        }
    }

    /// <summary>Alt+F1 / Alt+F2: drives with free space, then places (SPEC §6).</summary>
    private async Task ShowDriveMenuAsync(FilePanel panel)
    {
        var previouslyActive = _active;
        var culture = CultureInfo.CurrentCulture;
        var currentRoot = Path.GetPathRoot(panel.CurrentPath) ?? string.Empty;
        var items = new List<QuickPickItem>();
        var drives = DriveCatalog.List();
        foreach (var drive in drives)
        {
            items.Add(new QuickPickItem
            {
                Label = drive.Root.TrimEnd('\\'),
                Description = drive.Type.ToString(),
                Icon = drive.Type switch
                {
                    DriveType.Network => "",
                    DriveType.Removable => "",
                    DriveType.CDRom => "",
                    _ => "",
                },
                Hotkey = drive.Root[0],
                Tag = drive,
                IsCurrent = string.Equals(drive.Root, currentRoot, StringComparison.OrdinalIgnoreCase),
                Detail = "…",
            });
        }

        var places = DriveCatalog.Places().ToList();
        places.Add(("Other panel", Other(panel).CurrentPath));
        var first = true;
        foreach (var (name, path) in places)
        {
            items.Add(new QuickPickItem
            {
                Label = name,
                Description = path,
                Group = first ? "Places" : null,
                Icon = name switch { "Home" => "", "Downloads" => "", "Documents" => "", "Desktop" => "", _ => "" },
                Tag = path,
            });
            first = false;
        }

        // Sizes arrive per drive; slow or disconnected drives never block the menu.
        foreach (var item in items.Where(i => i.Tag is DriveSummary))
        {
            _ = FillDriveAsync(item, culture);
        }

        var result = await QuickPick.ShowAsync(panel, items, new QuickPickOptions
        {
            Title = $"Change drive ({panel.Side.ToLowerInvariant()} panel)",
            Placeholder = "Drive letter or name",
            OnDelete = item => DisconnectOrEject(item),
        });

        string? target = result?.Item?.Tag switch
        {
            DriveSummary drive => _lastPathPerDrive.TryGetValue(drive.Root, out var last) && Directory.Exists(last) ? last : drive.Root,
            string path => path,
            _ => null,
        };

        if (target is null)
        {
            // Cancelled: focus returns to the panel that was active (Far).
            previouslyActive.Focus();
            return;
        }

        Activate(panel);
        panel.Focus();
        await panel.NavigateAsync(target);
        SyncShellDirectory();
    }

    private static async Task FillDriveAsync(QuickPickItem item, CultureInfo culture)
    {
        var drive = await DriveCatalog.QueryAsync((DriveSummary)item.Tag!);
        if (!drive.IsReady)
        {
            item.Detail = "not ready";
            return;
        }

        item.Description = string.IsNullOrEmpty(drive.Label) ? $"{drive.Type} · {drive.FileSystem}" : $"{drive.Label} · {drive.FileSystem}";
        item.Detail = $"{PanelColumns.FormatCompactSize(drive.FreeBytes ?? 0, culture)} free of {PanelColumns.FormatCompactSize(drive.TotalBytes ?? 0, culture)}";
        item.Usage = drive.UsedFraction;
    }

    private void DisconnectOrEject(QuickPickItem item)
    {
        if (item.Tag is not DriveSummary drive || drive.Type is not (DriveType.Network or DriveType.Removable))
        {
            return;
        }

        var action = drive.Type == DriveType.Network ? "Disconnect network drive" : "Eject";
        if (MessageBox.Show(this, $"{action} {drive.Root.TrimEnd('\\')}?", "Habilis Terminal", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            if (drive.Type == DriveType.Network)
            {
                DriveCatalog.DisconnectNetworkDrive(drive.Root);
            }
            else
            {
                ShellVerbs.Eject(drive.Root);
            }

            item.Detail = drive.Type == DriveType.Network ? "disconnected" : "ejected";
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            item.Detail = ex.Message;
        }
    }
}
