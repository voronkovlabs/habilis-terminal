using System.ComponentModel;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Interop;
using HabilisTerminal.Core.Safety;

namespace HabilisTerminal.Core.Operations;

/// <summary>
/// Shared machinery of file operations (SPEC §8): progress, pause / cancel checkpoints, the error dialog loop
/// (Retry / Skip / Skip all / Cancel) with the locking process, confirmations with "to all", and the safety guard.
/// Operations run synchronously on a worker thread; the handler blocks it while the user answers. Copying may use
/// several threads (issue #392): progress, results and the questions to the user are safe to use from any of them,
/// and only one question is asked at a time.
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
    private readonly Dictionary<string, long> _partials = new(StringComparer.Ordinal);
    private readonly object _progressGate = new();
    private readonly object _questionGate = new();

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
        lock (_progressGate)
        {
            _totalBytes = bytes;
            _totalItems = items;
        }

        Report(null, force: true);
    }

    /// <summary>An item is done; its partial progress, if any, is replaced by <paramref name="bytes"/>.</summary>
    protected void AddDone(long bytes, int items, string? current)
    {
        lock (_progressGate)
        {
            _doneBytes += bytes;
            _doneItems += items;
            if (current is not null)
            {
                _partials.Remove(current);
            }
        }

        Report(current);
    }

    /// <summary>Progress inside one file: <paramref name="fileBytes"/> copied so far (the first chunk of each file is always reported).</summary>
    protected void ReportPartial(long fileBytes, string current)
    {
        bool first;
        lock (_progressGate)
        {
            first = !_partials.ContainsKey(current);
            _partials[current] = fileBytes;
        }

        Report(current, force: first);
    }

    /// <summary>Changes the result under a lock: copy threads report at the same time.</summary>
    protected void Record(Action<OperationResult> change)
    {
        lock (Result)
        {
            change(Result);
        }
    }

    private void Report(string? current, bool force = false)
    {
        OperationProgress progress;
        lock (_progressGate)
        {
            var now = DateTime.UtcNow;
            if (!force && now - _lastReport < TimeSpan.FromMilliseconds(50))
            {
                return;
            }

            _lastReport = now;
            var partial = 0L;
            foreach (var bytes in _partials.Values)
            {
                partial += bytes;
            }

            progress = new OperationProgress(_doneBytes + partial, _totalBytes, _doneItems, _totalItems, current);
        }

        Handler.ReportProgress(progress);
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
                ErrorAction answer;
                lock (_questionGate)
                {
                    if (_skipAllErrors)
                    {
                        Record(r => r.Failed++);
                        return false;
                    }

                    var lockers = IsSharingViolation(ex) ? RestartManager.LockingProcesses(path) : null;
                    answer = Handler.ResolveError(new OperationError(path, verb, ex, lockers));
                    if (answer == ErrorAction.SkipAll)
                    {
                        _skipAllErrors = true;
                    }
                }

                switch (answer)
                {
                    case ErrorAction.Retry:
                        continue;
                    case ErrorAction.SkipAll:
                    case ErrorAction.Skip:
                        Record(r => r.Failed++);
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
        lock (_questionGate)
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
    }

    /// <summary>Asks how to handle an existing target; one question at a time.</summary>
    protected ConflictDecision AskConflict(ConflictInfo conflict)
    {
        lock (_questionGate)
        {
            return Handler.ResolveConflict(conflict);
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
        var directories = new Stack<string>();
        foreach (var path in paths)
        {
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
                directories.Push(path);
            }
        }

        // One directory read gives the attributes and sizes of all its children (issue #392).
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
        while (directories.Count > 0)
        {
            Control.Checkpoint();
            try
            {
                foreach (var child in new DirectoryInfo(directories.Pop()).EnumerateFileSystemInfos("*", options))
                {
                    items++;
                    var attributes = child.Attributes;
                    if ((attributes & FileAttributes.Directory) == 0)
                    {
                        bytes += ((FileInfo)child).Length;
                    }
                    else if (followLinks || (attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        directories.Push(child.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return (bytes, items);
    }

    protected static FileEntry EntryOf(string path) =>
        DirectoryReader.ReadEntry(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        ?? new FileEntry(Path.GetFileName(path), FileAttributes.Normal, 0, default, default, default);
}
