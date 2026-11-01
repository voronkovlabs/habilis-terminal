using System.ComponentModel;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Interop;
using HabilisTerminal.Core.Safety;

namespace HabilisTerminal.Core.Operations;

/// <summary>
/// Shared machinery of file operations (SPEC §8): progress, pause / cancel checkpoints, the error dialog loop
/// (Retry / Skip / Skip all / Cancel) with the locking process, confirmations with "to all", and the safety guard.
/// Operations run synchronously on a worker thread; the handler blocks it while the user answers.
/// </summary>
public abstract class FileOperation
{
    private readonly Dictionary<ConfirmationKind, bool> _answerForAll = [];
    private bool _skipAllErrors;
    private long _doneBytes;
    private long _totalBytes;
    private int _doneItems;
    private int _totalItems;
    private DateTime _lastReport;
    private string? _partialFile;

    protected FileOperation(IOperationHandler handler, OperationControl control)
    {
        Handler = handler;
        Control = control;
    }

    public OperationResult Result { get; } = new();

    protected IOperationHandler Handler { get; }

    protected OperationControl Control { get; }

    /// <summary>Runs the operation; throws <see cref="OperationCanceledException"/> when cancelled.</summary>
    public OperationResult Run()
    {
        Execute();
        Report(null, force: true);
        return Result;
    }

    protected abstract void Execute();

    protected void SetTotals(long bytes, int items)
    {
        _totalBytes = bytes;
        _totalItems = items;
        Report(null, force: true);
    }

    protected void AddDone(long bytes, int items, string? current)
    {
        _doneBytes += bytes;
        _doneItems += items;
        Report(current);
    }

    /// <summary>Progress inside one file: <paramref name="fileBytes"/> copied so far (the first chunk of each file is always reported).</summary>
    protected void ReportPartial(long fileBytes, string current)
    {
        var first = !string.Equals(_partialFile, current, StringComparison.Ordinal);
        _partialFile = current;
        Report(current, force: first, extraBytes: fileBytes);
    }

    private void Report(string? current, bool force = false, long extraBytes = 0)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastReport < TimeSpan.FromMilliseconds(50))
        {
            return;
        }

        _lastReport = now;
        Handler.ReportProgress(new OperationProgress(_doneBytes + extraBytes, _totalBytes, _doneItems, _totalItems, current));
    }

    /// <summary>
    /// Runs <paramref name="action"/>, asking the user on failure. Returns false when the item was skipped.
    /// </summary>
    protected bool Attempt(string path, string verb, Action action)
    {
        while (true)
        {
            Control.Checkpoint();
            try
            {
                action();
                return true;
            }
            catch (Exception ex) when (IsFileSystemError(ex))
            {
                if (_skipAllErrors)
                {
                    Result.Failed++;
                    return false;
                }

                var lockers = IsSharingViolation(ex) ? RestartManager.LockingProcesses(path) : null;
                switch (Handler.ResolveError(new OperationError(path, verb, ex, lockers)))
                {
                    case ErrorAction.Retry:
                        continue;
                    case ErrorAction.SkipAll:
                        _skipAllErrors = true;
                        Result.Failed++;
                        return false;
                    case ErrorAction.Skip:
                        Result.Failed++;
                        return false;
                    default:
                        throw new OperationCanceledByUserException();
                }
            }
        }
    }

    /// <summary>Asks a yes/no question, remembering "Yes to all" / "No to all"; Cancel stops the operation.</summary>
    protected bool Confirm(Confirmation confirmation)
    {
        if (_answerForAll.TryGetValue(confirmation.Kind, out var remembered))
        {
            return remembered;
        }

        switch (Handler.Confirm(confirmation))
        {
            case ConfirmationAnswer.Yes:
                return true;
            case ConfirmationAnswer.YesToAll:
                _answerForAll[confirmation.Kind] = true;
                return true;
            case ConfirmationAnswer.No:
                return false;
            default:
                throw new OperationCanceledByUserException();
        }
    }

    /// <summary>Every path an operation changes or deletes passes the guard first (tests restrict it to their sandbox).</summary>
    protected static void Guard(string path) => DestructiveOperationGuard.Check(path);

    protected static bool IsProtected(FileAttributes attributes) =>
        (attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0;

    protected static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static bool IsFileSystemError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or Win32Exception or System.Security.SecurityException
        && ex is not OperationCanceledException;

    private static bool IsSharingViolation(Exception ex) =>
        (ex.HResult & 0xFFFF) is NativeFile.ErrorSharingViolation or NativeFile.ErrorLockViolation ||
        ex is Win32Exception { NativeErrorCode: NativeFile.ErrorSharingViolation or NativeFile.ErrorLockViolation };

    /// <summary>Counts bytes and items below the given paths, not entering links unless asked.</summary>
    protected (long Bytes, int Items) Scan(IEnumerable<string> paths, bool followLinks)
    {
        long bytes = 0;
        var items = 0;
        var pending = new Stack<string>(paths);
        while (pending.Count > 0)
        {
            Control.Checkpoint();
            var path = pending.Pop();
            items++;
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                try
                {
                    bytes += new FileInfo(path).Length;
                }
                catch (IOException)
                {
                }
            }
            else if (followLinks || (attributes & FileAttributes.ReparsePoint) == 0)
            {
                try
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
                    {
                        pending.Push(child);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return (bytes, items);
    }

    protected static FileEntry EntryOf(string path) =>
        DirectoryReader.ReadEntry(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        ?? new FileEntry(Path.GetFileName(path), FileAttributes.Normal, 0, default, default, default);
}
