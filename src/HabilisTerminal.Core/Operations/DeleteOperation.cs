using HabilisTerminal.Core.Interop;

namespace HabilisTerminal.Core.Operations;

/// <summary>
/// F8 / Shift+Del (SPEC §8). To the Recycle Bin when possible; when not, the user is asked before anything is
/// deleted permanently. Permanent deletion walks the tree itself: links are deleted as links and never entered.
/// </summary>
public sealed class DeleteOperation : FileOperation
{
    private readonly string[] _paths;
    private readonly bool _permanent;

    public DeleteOperation(IReadOnlyList<string> paths, bool permanent, IOperationHandler handler, OperationControl control)
        : base(handler, control)
    {
        _paths = paths.Select(Path.GetFullPath).ToArray();
        _permanent = permanent;
    }

    protected override void Execute()
    {
        var (bytes, items) = Scan(_paths, followLinks: false);
        SetTotals(bytes, items);

        foreach (var path in _paths)
        {
            Control.Checkpoint();
            Guard(path);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                continue;
            }

            if (!_permanent)
            {
                var reason = RecycleBin.CannotRecycleReason(path);
                if (reason is null)
                {
                    var (itemBytes, itemCount) = Scan([path], followLinks: false);
                    if (Attempt(path, "move to Recycle Bin", () => RecycleBin.Recycle(path)))
                    {
                        Result.Succeeded++;
                    }

                    AddDone(itemBytes, itemCount, path);
                    continue;
                }

                if (!Confirm(new Confirmation(ConfirmationKind.DeletePermanently, path, reason)))
                {
                    Result.Skipped++;
                    continue;
                }
            }

            var attributes = File.GetAttributes(path);
            var isPlainDirectory = (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0;
            if (isPlainDirectory && Directory.EnumerateFileSystemEntries(path).Any() &&
                !Confirm(new Confirmation(ConfirmationKind.DeleteNonEmptyDirectory, path)))
            {
                Result.Skipped++;
                continue;
            }

            if (DeleteTree(path))
            {
                Result.Succeeded++;
            }
        }
    }

    /// <summary>Deletes a file, a link (only the link) or a directory tree; false when something was left.</summary>
    private bool DeleteTree(string path)
    {
        Control.Checkpoint();
        Guard(path);
        var attributes = File.GetAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var isLink = (attributes & FileAttributes.ReparsePoint) != 0;

        if (!isDirectory || isLink)
        {
            if (!isDirectory && IsProtected(attributes) &&
                !Confirm(new Confirmation(ConfirmationKind.DeleteProtected, path)))
            {
                Result.Skipped++;
                AddDone(0, 1, path);
                return false;
            }

            long size = isDirectory ? 0 : new FileInfo(path).Length;
            var deleted = Attempt(path, "delete", () =>
            {
                ClearReadOnly(path);
                if (isDirectory)
                {
                    // A junction or directory symlink: removes the link, the target stays untouched.
                    Directory.Delete(path);
                }
                else
                {
                    File.Delete(path);
                }
            });
            AddDone(size, 1, path);
            return deleted;
        }

        var complete = true;
        string[] children = [];
        Attempt(path, "read", () => children = Directory.GetFileSystemEntries(path, "*", new EnumerationOptions { AttributesToSkip = 0 }));
        foreach (var child in children)
        {
            complete &= DeleteTree(child);
        }

        if (!complete)
        {
            AddDone(0, 1, path);
            return false;
        }

        var removed = Attempt(path, "delete", () =>
        {
            ClearReadOnly(path);
            Directory.Delete(path);
        });
        AddDone(0, 1, path);
        return removed;
    }
}
