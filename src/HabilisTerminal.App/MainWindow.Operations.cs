using System.IO;
using System.Windows;
using HabilisTerminal.App.Operations;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Operations;

namespace HabilisTerminal.App;

/// <summary>File operations from the panels (SPEC §8): dialogs, background runs with progress, cursor rules.</summary>
public partial class MainWindow
{
    private readonly OperationScheduler _scheduler = new();
    private OperationToasts? _toasts;

    /// <summary>F8 target until settings exist (stage 10): <c>HABI_DELETE_TO_RECYCLE_BIN=0</c> deletes permanently.</summary>
    private static bool DeleteToRecycleBin => Environment.GetEnvironmentVariable("HABI_DELETE_TO_RECYCLE_BIN") != "0";

    private OperationToasts Toasts => _toasts ??= new OperationToasts(this, Workspace);

    /// <summary>F5 / F6; with <paramref name="inPlace"/> (Shift) only the item under the cursor, prefilled with its name.</summary>
    private async Task CopyOrMoveAsync(FilePanel source, bool move, bool inPlace)
    {
        var entries = inPlace ? (source.State.Current.IsParent ? [] : [source.State.Current.Entry]) : source.State.TargetEntries();
        if (entries.Count == 0)
        {
            return;
        }

        var target = Other(source);
        var names = entries.Select(e => e.Name).ToArray();
        var initial = inPlace ? names[0] : target.CurrentPath.TrimEnd('\\') + "\\";
        var choice = OperationDialogs.CopyOrMove(this, move, names, initial);
        source.Focus();
        if (choice is not { } chosen)
        {
            return;
        }

        var destination = Path.IsPathRooted(chosen.Destination) ? chosen.Destination : Path.Combine(source.CurrentPath, chosen.Destination);
        var sources = names.Select(n => Path.Combine(source.CurrentPath, n)).ToArray();
        var title = $"{(move ? "Moving" : "Copying")} {(names.Length == 1 ? names[0] : $"{names.Length} items")}";
        var result = await RunAsync(title, [.. sources, destination], (handler, control) =>
            new CopyOperation(sources, destination, chosen.Options, move, handler, control).Run());

        source.State.Selection.Clear();
        await source.ReloadAsync();
        await target.ReloadAsync();
        if (result?.TargetPaths.LastOrDefault() is { } last)
        {
            // The cursor goes to the last copied item in whichever panel shows it (Far).
            foreach (var panel in new[] { target, source })
            {
                if (string.Equals(Path.GetDirectoryName(last), panel.CurrentPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    panel.State.TrySetCursor(Path.GetFileName(last));
                    panel.Refresh();
                    break;
                }
            }
        }
    }

    private async Task DeleteAsync(FilePanel panel, bool permanent)
    {
        var entries = panel.State.TargetEntries();
        if (entries.Count == 0)
        {
            return;
        }

        var names = entries.Select(e => e.Name).ToArray();
        if (!OperationDialogs.ConfirmDelete(this, names, permanent))
        {
            panel.Focus();
            return;
        }

        panel.Focus();

        // After deletion the cursor goes to the item following the deleted ones (Far).
        var deleted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        string? next = null;
        for (var i = panel.State.CursorIndex; i < panel.State.Count; i++)
        {
            if (!deleted.Contains(panel.State.ItemAt(i).Name))
            {
                next = panel.State.ItemAt(i).Name;
                break;
            }
        }

        next ??= Enumerable.Range(0, panel.State.Count).Select(i => panel.State.ItemAt(i).Name).LastOrDefault(n => !deleted.Contains(n));
        var paths = names.Select(n => Path.Combine(panel.CurrentPath, n)).ToArray();
        var title = $"{(permanent ? "Deleting" : "Moving to Recycle Bin")} {(names.Length == 1 ? names[0] : $"{names.Length} items")}";
        await RunAsync(title, paths, (handler, control) => new DeleteOperation(paths, permanent, handler, control).Run());

        panel.State.Selection.Clear();
        await panel.ReloadAsync();
        if (next is not null)
        {
            panel.State.TrySetCursor(next);
            panel.Refresh();
        }
    }

    private async Task MakeDirectoryAsync(FilePanel panel)
    {
        var input = OperationDialogs.MakeDirectory(this);
        panel.Focus();
        if (input is not { } chosen)
        {
            return;
        }

        try
        {
            var created = DirectoryCreator.Create(panel.CurrentPath, chosen.Text, chosen.Multiple);
            await panel.ReloadAsync();
            var first = Path.GetRelativePath(panel.CurrentPath, created[0]).Split('\\')[0];
            panel.State.TrySetCursor(first);
            panel.Refresh();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            OperationDialogs.Message(this, "Create folder", ex.Message);
            panel.Focus();
        }
    }

    private async Task EditAttributesAsync(FilePanel panel)
    {
        var entries = panel.State.TargetEntries();
        if (entries.Count == 0)
        {
            return;
        }

        var paths = entries.Select(e => Path.Combine(panel.CurrentPath, e.Name)).ToArray();
        var change = OperationDialogs.Attributes(this, paths);
        panel.Focus();
        if (change is null)
        {
            return;
        }

        await RunAsync("Setting attributes", paths, (handler, control) => new AttributesOperation(paths, change, handler, control).Run());
        await panel.ReloadAsync();
    }

    /// <summary>F9 → Commands → Compare folders (SPEC §8).</summary>
    private void CompareFolders()
    {
        var fat = FolderComparer.IsFatVolume(_left.CurrentPath) || FolderComparer.IsFatVolume(_right.CurrentPath);
        var (left, right) = FolderComparer.Compare(_left.State.Entries, _right.State.Entries, compareSize: true, fatTimePrecision: fat);
        foreach (var (panel, names) in new[] { (_left, left), (_right, right) })
        {
            panel.State.Selection.Clear();
            foreach (var name in names)
            {
                panel.State.Selection.Set(name, true);
            }

            panel.Refresh();
        }

        if (left.Count == 0 && right.Count == 0)
        {
            _active.Message = "The folders are identical.";
        }
    }

    /// <summary>Runs an operation in the background (queued per disk) with a progress card; null when cancelled or refused.</summary>
    private async Task<OperationResult?> RunAsync(string title, IReadOnlyList<string> paths, Func<IOperationHandler, OperationControl, OperationResult> work)
    {
        using var control = new OperationControl();
        var card = Toasts.Add(title, control);
        var handler = new UiOperationHandler(this, card);
        if (paths.Any(_scheduler.IsBusy))
        {
            card.Queued();
        }

        try
        {
            return await _scheduler.RunAsync(paths, () => work(handler, control), control.Token);
        }
        catch (OperationRefusedException ex)
        {
            OperationDialogs.Message(this, title, ex.Message);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            OperationDialogs.Message(this, title, ex.Message);
            return null;
        }
        finally
        {
            card.Close();
        }
    }
}
