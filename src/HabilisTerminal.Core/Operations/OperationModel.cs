using HabilisTerminal.Core.FileSystem;

namespace HabilisTerminal.Core.Operations;

/// <summary>What to do when the target file already exists (F5 / F6 dialog, SPEC §8).</summary>
public enum ExistingFilePolicy
{
    Ask,
    Overwrite,
    Skip,
    Rename,
    Append,
    OnlyNewer,
}

public enum ConflictAction
{
    Overwrite,
    Skip,

    /// <summary>Write under another name (<see cref="ConflictDecision.NewName"/>, or <c>name (2).ext</c>).</summary>
    Rename,

    /// <summary>Append the source to the end of the target.</summary>
    Append,

    /// <summary>Overwrite only when the source is newer.</summary>
    OverwriteIfNewer,
    Cancel,
}

public sealed record ConflictInfo(string SourcePath, FileEntry Source, string TargetPath, FileEntry Target);

/// <param name="ApplyToAll">Use the same action for every later conflict of this operation.</param>
public sealed record ConflictDecision(ConflictAction Action, bool ApplyToAll = false, string? NewName = null);

public enum ErrorAction
{
    Retry,
    Skip,
    SkipAll,
    Cancel,
}

/// <param name="Operation">Short verb shown to the user: "copy", "delete", "set attributes"…</param>
/// <param name="LockingProcesses">Processes holding the file, from the Restart Manager, when the file is in use.</param>
public sealed record OperationError(string Path, string Operation, Exception Exception, string? LockingProcesses = null)
{
    public string Message => LockingProcesses is null ? Exception.Message : $"{Exception.Message} (in use by {LockingProcesses})";
}

public enum ConfirmationKind
{
    /// <summary>The target is read-only / hidden / system: overwrite it anyway?</summary>
    OverwriteProtected,

    /// <summary>Delete a read-only / hidden / system file?</summary>
    DeleteProtected,

    /// <summary>Delete a directory that is not empty (permanent delete)?</summary>
    DeleteNonEmptyDirectory,

    /// <summary>The item cannot go to the Recycle Bin: delete it permanently?</summary>
    DeletePermanently,

    /// <summary>A symbolic link cannot be recreated (no privilege): copy what it points to instead?</summary>
    CopyLinkContents,

    /// <summary>Not enough free space on the target: start anyway?</summary>
    InsufficientSpace,
}

/// <param name="Detail">Extra text: the reason a Recycle Bin is unavailable, the sizes for free space, etc.</param>
public sealed record Confirmation(ConfirmationKind Kind, string Path, string? Detail = null);

public enum ConfirmationAnswer
{
    Yes,
    YesToAll,
    No,
    Cancel,
}

/// <summary>Progress in bytes and items; <see cref="TotalBytes"/> is known after the initial scan.</summary>
public sealed record OperationProgress(long DoneBytes, long TotalBytes, int DoneItems, int TotalItems, string? CurrentPath);

/// <summary>
/// The user side of an operation. Calls arrive on the worker thread and block it until answered,
/// so the UI can show a dialog and tests can answer synchronously.
/// </summary>
public interface IOperationHandler
{
    ConflictDecision ResolveConflict(ConflictInfo conflict);

    ErrorAction ResolveError(OperationError failure);

    ConfirmationAnswer Confirm(Confirmation confirmation);

    void ReportProgress(OperationProgress progress);
}

/// <summary>Raised when the user chooses Cancel in a conflict, error or confirmation.</summary>
public sealed class OperationCanceledByUserException() : OperationCanceledException("The operation was cancelled.");

/// <summary>Pause / resume and cancellation shared by an operation and its UI.</summary>
public sealed class OperationControl : IDisposable
{
    private readonly ManualResetEventSlim _running = new(initialState: true);
    private readonly CancellationTokenSource _cancellation = new();

    public CancellationToken Token => _cancellation.Token;

    public bool IsPaused => !_running.IsSet;

    public void Pause() => _running.Reset();

    public void Resume() => _running.Set();

    public void Cancel()
    {
        _cancellation.Cancel();
        _running.Set();
    }

    public void Dispose()
    {
        _running.Dispose();
        _cancellation.Dispose();
    }

    /// <summary>Blocks while paused; throws when cancelled.</summary>
    public void Checkpoint()
    {
        _running.Wait(Token);
        Token.ThrowIfCancellationRequested();
    }
}

/// <summary>What an operation did, for the panels (cursor placement) and the final notification.</summary>
public sealed class OperationResult
{
    public int Succeeded { get; internal set; }

    public int Skipped { get; internal set; }

    public int Failed { get; internal set; }

    public long Bytes { get; internal set; }

    /// <summary>Target paths created or written (last one gets the cursor after copy / move).</summary>
    public List<string> TargetPaths { get; } = [];
}
