using HabilisTerminal.Core.FileSystem;
using System.ComponentModel;
using HabilisTerminal.Core.Interop;

namespace HabilisTerminal.Core.Operations;

public sealed record CopyOptions
{
    public ExistingFilePolicy ExistingFiles { get; init; } = ExistingFilePolicy.Ask;

    /// <summary>Copy ACLs instead of inheriting them from the target (default off, as Explorer).</summary>
    public bool CopyAccessRights { get; init; }

    /// <summary>Copy what links point to instead of recreating the links (default off).</summary>
    public bool CopyLinkContents { get; init; }

    /// <summary>
    /// How many files are copied at the same time (issue #392); a spinning disk on either side always gets one.
    /// </summary>
    public int Threads { get; init; } = 1;
}

/// <summary>The operation refuses to start (e.g. copying a directory into itself).</summary>
public sealed class OperationRefusedException(string message) : InvalidOperationException(message);

/// <summary>
/// F5 copy and F6 move / rename (SPEC §8). Same volume moves are renames; everything else is copied with
/// <c>CopyFileEx</c> and, for moves, the source is deleted per item only after that item was copied.
/// Links are recreated (junctions as junctions, symlinks as symlinks) and never entered unless asked.
/// The walk over the tree, folders and questions about existing targets stay on the operation's thread; with
/// <see cref="CopyOptions.Threads"/> above one the files themselves are copied by <see cref="CopyWorkers"/>, and
/// folder times are set once all files are in (issue #392).
/// </summary>
public sealed class CopyOperation : FileOperation
{
    private readonly string[] _sources;
    private readonly string _destination;
    private readonly CopyOptions _options;
    private readonly bool _move;
    private ConflictDecision? _conflictForAll;
    private CopyWorkers? _workers;
    private readonly List<Action> _finishLater = [];
    private readonly List<(long Order, string Path)> _targets = [];
    private long _order;

    public CopyOperation(IReadOnlyList<string> sources, string destination, CopyOptions options, bool move, IOperationHandler handler, OperationControl control)
        : base(handler, control)
    {
        _sources = sources.Select(Path.GetFullPath).ToArray();
        _destination = destination;
        _options = options;
        _move = move;
    }

    /// <summary>Tests only: take the copy-and-delete path even within one volume.</summary>
    internal bool ForceCrossVolume { get; init; }

    /// <summary>Tests only: use <see cref="CopyOptions.Threads"/> whatever the disks are.</summary>
    internal bool IgnoreSeekPenalty { get; init; }

    /// <summary>
    /// Where each source goes (SPEC §8): into an existing directory or one ending with <c>\</c>; a new name for a
    /// single source; a new directory for several sources; Far masks such as <c>*.bak</c> in the last segment.
    /// </summary>
    public static IReadOnlyList<(string Source, string Target)> ResolveTargets(IReadOnlyList<string> sources, string destination)
    {
        var endsWithSeparator = destination.Length > 0 && PathText.IsSeparator(destination[^1]);
        var full = Path.GetFullPath(destination);
        var lastSegment = Path.GetFileName(full.TrimEndSeparator());
        if (lastSegment.AsSpan().IndexOfAny('*', '?') >= 0)
        {
            var directory = Path.GetDirectoryName(full.TrimEndSeparator())!;
            return sources.Select(s => (s, Path.Combine(directory, NameRules.ApplyRenameMask(Path.GetFileName(s.TrimEndSeparator()), lastSegment)))).ToArray();
        }

        if (endsWithSeparator || Directory.Exists(full) || sources.Count > 1)
        {
            return sources.Select(s => (s, Path.Combine(full, Path.GetFileName(s.TrimEndSeparator())))).ToArray();
        }

        return [(sources[0], full)];
    }

    protected override void Execute()
    {
        var targets = ResolveTargets(_sources, _destination);
        foreach (var (source, target) in targets)
        {
            if (Directory.Exists(source) && IsSameOrInside(target, source) && !string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                throw new OperationRefusedException($"Cannot {(_move ? "move" : "copy")} '{source}' into itself.");
            }

            if (NameRules.Validate(Path.GetFileName(target)) is { } nameError)
            {
                throw new OperationRefusedException(nameError);
            }
        }

        var (bytes, items) = Scan(_sources, _options.CopyLinkContents);
        SetTotals(bytes, items);

        var anyCopy = targets.Any(t => !_move || ForceCrossVolume || !NativeFile.SameVolume(t.Source, Path.GetDirectoryName(t.Target)!));
        if (anyCopy && targets.Count > 0)
        {
            var targetDirectory = Path.GetDirectoryName(targets[0].Target)!;
            var probe = Directory.Exists(targetDirectory) ? targetDirectory : Path.GetPathRoot(targetDirectory)!;
            if (NativeFile.FreeBytes(probe) is { } free && free < bytes &&
                !Confirm(new Confirmation(ConfirmationKind.InsufficientSpace, targetDirectory, $"{bytes:N0} bytes needed, {free:N0} free")))
            {
                throw new OperationCanceledByUserException();
            }
        }

        var threads = anyCopy && targets.Count > 0 ? ThreadsFor(targets[0].Source, Path.GetDirectoryName(targets[0].Target)!) : 1;
        _workers = threads > 1 ? new CopyWorkers(threads, Control.Token) : null;
        try
        {
            foreach (var (source, target) in targets)
            {
                Control.Checkpoint();
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (_move && !ForceCrossVolume && NativeFile.SameVolume(source, Path.GetDirectoryName(target)!))
                {
                    MoveWithinVolume(source, target);
                }
                else
                {
                    CopyItem(source, target);
                }
            }

            _workers?.Complete();
            foreach (var finish in _finishLater)
            {
                Control.Checkpoint();
                finish();
            }
        }
        catch
        {
            _workers?.Abort();
            throw;
        }
        finally
        {
            _workers?.Dispose();
            _workers = null;
            lock (_targets)
            {
                Result.TargetPaths.AddRange(_targets.OrderBy(t => t.Order).Select(t => t.Path));
            }
        }
    }

    /// <summary>Several files at once, unless a side is a spinning disk or cannot be told (a network share can).</summary>
    private int ThreadsFor(string source, string targetDirectory)
    {
        var wanted = Math.Clamp(_options.Threads, 1, 64);
        if (wanted == 1)
        {
            return 1;
        }

        if (IgnoreSeekPenalty)
        {
            return wanted;
        }

        var probe = Directory.Exists(targetDirectory) ? targetDirectory : Path.GetPathRoot(targetDirectory)!;
        return NativeFile.HasSeekPenalty(source) == false && NativeFile.HasSeekPenalty(probe) == false ? wanted : 1;
    }

    /// <summary>Runs a file's copy into <paramref name="folder"/> here or on a copy thread.</summary>
    private void Run(string folder, Action work)
    {
        if (_workers is { } workers)
        {
            workers.Post(folder, work);
        }
        else
        {
            work();
        }
    }

    /// <summary>A place in the order the items were walked, so the last target is the last one walked.</summary>
    private long NextOrder() => Interlocked.Increment(ref _order);

    private void AddTarget(long order, string path)
    {
        lock (_targets)
        {
            _targets.Add((order, path));
        }
    }

    private static bool IsSameOrInside(string path, string directory)
    {
        return PathText.IsSameOrInside(path, directory);
    }

    private void MoveWithinVolume(string source, string target)
    {
        Guard(source);
        Guard(target);
        var caseOnlyRename = string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
        var sourceIsDirectory = Directory.Exists(source);
        if (!caseOnlyRename && (File.Exists(target) || Directory.Exists(target)))
        {
            if (sourceIsDirectory && Directory.Exists(target))
            {
                // Merge into the existing directory item by item, then remove the emptied source.
                foreach (var child in Directory.EnumerateFileSystemEntries(source, "*", new EnumerationOptions { AttributesToSkip = 0 }).ToArray())
                {
                    MoveWithinVolume(child, Path.Combine(target, Path.GetFileName(child)));
                }

                if (!Directory.EnumerateFileSystemEntries(source).Any())
                {
                    Attempt(source, "remove", () => Directory.Delete(source));
                }

                return;
            }

            var resolved = ResolveExistingTarget(source, target);
            if (resolved is null)
            {
                return;
            }

            if (resolved.Value.Append)
            {
                CopyItem(source, target);
                return;
            }

            target = resolved.Value.Target;
        }

        var size = sourceIsDirectory ? 0 : new FileInfo(source).Length;
        var order = NextOrder();
        if (Attempt(source, "move", () => NativeFile.Move(source, target)))
        {
            Record(r =>
            {
                r.Succeeded++;
                r.Bytes += size;
            });
            AddTarget(order, target);
        }

        AddDone(size, 1, source);
    }

    /// <param name="info">What the folder read said about the source, so it is not asked again (issue #392).</param>
    /// <param name="fresh">The target's folder was created by this copy, so nothing is in the way.</param>
    private void CopyItem(string source, string target, FileSystemInfo? info = null, bool fresh = false)
    {
        Control.Checkpoint();
        FileAttributes attributes;
        if (info is not null)
        {
            attributes = info.Attributes;
        }
        else
        {
            try
            {
                attributes = File.GetAttributes(source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Attempt(source, "read", () => File.GetAttributes(source));
                return;
            }
        }

        var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
        if ((attributes & FileAttributes.Directory) != 0)
        {
            if (isLink && !_options.CopyLinkContents && RecreateDirectoryLink(source, target))
            {
                return;
            }

            CopyDirectory(source, target, info as DirectoryInfo, fresh);
        }
        else
        {
            CopyFile(source, target, copyLinkItself: isLink && !_options.CopyLinkContents, (info as FileInfo)?.Length, fresh);
        }
    }

    private void CopyDirectory(string source, string target, DirectoryInfo? info, bool fresh)
    {
        Guard(target);
        var existed = false;
        if (!fresh)
        {
            if (File.Exists(target))
            {
                Attempt(target, "create folder", () => throw new IOException($"A file named '{Path.GetFileName(target)}' already exists."));
                AddDone(0, 1, source);
                return;
            }

            existed = Directory.Exists(target);
        }

        if (!Attempt(target, "create folder", () => Directory.CreateDirectory(target)))
        {
            AddDone(0, 1, source);
            return;
        }

        // The source's times and attributes as they were before its files were copied (or moved out).
        if (info is null)
        {
            info = new DirectoryInfo(source);
            info.Refresh();
        }

        FileSystemInfo[] children = [];
        Attempt(source, "read", () => children = new DirectoryInfo(source).GetFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }));
        foreach (var child in children)
        {
            CopyItem(child.FullName, Path.Combine(target, child.Name), child, fresh: !existed);
        }

        var order = NextOrder();
        var sourceInfo = info;
        void Finish()
        {
            Attempt(target, "set times", () =>
            {
                Directory.SetCreationTimeUtc(target, sourceInfo.CreationTimeUtc);
                Directory.SetLastWriteTimeUtc(target, sourceInfo.LastWriteTimeUtc);
                File.SetAttributes(target, sourceInfo.Attributes & ~FileAttributes.ReparsePoint);
            });

            AddDone(0, 1, source);
            AddTarget(order, target);
            if (_move && !Directory.EnumerateFileSystemEntries(source).Any())
            {
                Guard(source);
                Attempt(source, "remove", () =>
                {
                    ClearReadOnly(source);
                    Directory.Delete(source);
                });
            }
        }

        // Files still being copied would change the folder's time, and a moved folder is empty only once they are done.
        if (_workers is null)
        {
            Finish();
        }
        else
        {
            _finishLater.Add(Finish);
        }
    }

    private void CopyFile(string source, string target, bool copyLinkItself, long? length, bool fresh)
    {
        Guard(target);
        var append = false;
        if (!fresh && (File.Exists(target) || Directory.Exists(target)))
        {
            var resolved = ResolveExistingTarget(source, target);
            if (resolved is null)
            {
                return;
            }

            (target, append) = resolved.Value;
        }

        var size = length ?? new FileInfo(source).Length;
        var order = NextOrder();
        var to = target;
        Run(Path.GetDirectoryName(to)!, () => CopyFileNow(source, to, copyLinkItself, append, size, order));
    }

    private void CopyFileNow(string source, string target, bool copyLinkItself, bool append, long size, long order)
    {
        var copied = Attempt(source, "copy", () =>
        {
            if (append)
            {
                AppendFile(source, target);
                return;
            }

            try
            {
                NativeFile.CopyFile(source, target, copyLinkItself, failIfExists: false, length: size, progress: (done, _) =>
                {
                    ReportPartial(done, source);
                    try
                    {
                        Control.Checkpoint();
                        return true;
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == NativeFile.ErrorRequestAborted)
            {
                throw new OperationCanceledException("Copy cancelled.", ex);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == NativeFile.ErrorPrivilegeNotHeld && copyLinkItself)
            {
                if (!Confirm(new Confirmation(ConfirmationKind.CopyLinkContents, source, "creating symbolic links needs administrator rights or Developer Mode")))
                {
                    throw new IOException("Symbolic link skipped.", ex);
                }

                NativeFile.CopyFile(source, target, copyLinkItself: false, failIfExists: false, (done, _) => true);
            }

            if (_options.CopyAccessRights)
            {
                Platform.Platforms.Current.FileSystem.CopySecurity(source, target);
            }
        });

        if (copied)
        {
            Record(r =>
            {
                r.Succeeded++;
                r.Bytes += size;
            });
            AddTarget(order, target);
            if (_move)
            {
                Guard(source);
                Attempt(source, "delete", () =>
                {
                    ClearReadOnly(source);
                    File.Delete(source);
                });
            }
        }

        AddDone(size, 1, source);
    }

    /// <summary>Conflict handling for an existing target; null means skip.</summary>
    private (string Target, bool Append)? ResolveExistingTarget(string source, string target)
    {
        var sourceEntry = EntryOf(source);
        var targetEntry = EntryOf(target);
        var decision = _options.ExistingFiles switch
        {
            ExistingFilePolicy.Overwrite => new ConflictDecision(ConflictAction.Overwrite),
            ExistingFilePolicy.Skip => new ConflictDecision(ConflictAction.Skip),
            ExistingFilePolicy.Rename => new ConflictDecision(ConflictAction.Rename),
            ExistingFilePolicy.Append => new ConflictDecision(ConflictAction.Append),
            ExistingFilePolicy.OnlyNewer => new ConflictDecision(ConflictAction.OverwriteIfNewer),
            _ => _conflictForAll ?? AskConflict(new ConflictInfo(source, sourceEntry, target, targetEntry)),
        };

        if (decision.ApplyToAll)
        {
            _conflictForAll = decision with { NewName = null };
        }

        switch (decision.Action)
        {
            case ConflictAction.Skip:
                Skip(source, sourceEntry);
                return null;
            case ConflictAction.Cancel:
                throw new OperationCanceledByUserException();
            case ConflictAction.Rename:
                var directory = Path.GetDirectoryName(target)!;
                var newName = decision.NewName ?? NameRules.NextFreeName(Path.GetFileName(target), n => File.Exists(Path.Combine(directory, n)) || Directory.Exists(Path.Combine(directory, n)));
                return (Path.Combine(directory, newName), false);
            case ConflictAction.Append:
                return (target, true);
            case ConflictAction.OverwriteIfNewer when sourceEntry.LastWriteTimeUtc <= targetEntry.LastWriteTimeUtc:
                Skip(source, sourceEntry);
                return null;
        }

        // Overwrite: protected targets need a separate yes (Far).
        if (IsProtected(targetEntry.Attributes))
        {
            if (!Confirm(new Confirmation(ConfirmationKind.OverwriteProtected, target, FormatProtection(targetEntry.Attributes))))
            {
                Skip(source, sourceEntry);
                return null;
            }

            File.SetAttributes(target, targetEntry.Attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
        }

        return (target, false);
    }

    private void Skip(string source, Core.FileSystem.FileEntry entry)
    {
        Record(r => r.Skipped++);
        AddDone(entry.IsDirectory ? 0 : entry.Length, 1, source);
    }

    private static string FormatProtection(FileAttributes attributes) =>
        string.Join(", ", new[] { (FileAttributes.ReadOnly, "read-only"), (FileAttributes.Hidden, "hidden"), (FileAttributes.System, "system") }
            .Where(p => (attributes & p.Item1) != 0).Select(p => p.Item2));

    private void AppendFile(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var output = new FileStream(target, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 16);
        var buffer = new byte[1 << 16];
        long done = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            Control.Checkpoint();
            output.Write(buffer, 0, read);
            done += read;
            ReportPartial(done, source);
        }
    }

    /// <summary>Junctions are recreated as junctions, directory symlinks as symlinks; false means "copy contents instead".</summary>
    private bool RecreateDirectoryLink(string source, string target)
    {
        var linkTarget = new DirectoryInfo(source).LinkTarget;
        if (linkTarget is null)
        {
            return false;
        }

        Guard(target);
        var isJunction = NativeFile.ReparseTag(source) == NativeFile.ReparseTagMountPoint;
        if (!isJunction)
        {
            // Directory symlinks need a privilege; without it the user chooses between contents and skipping.
            try
            {
                Directory.CreateSymbolicLink(target, linkTarget);
                Record(r => r.Succeeded++);
                AddTarget(NextOrder(), target);
                AddDone(0, 1, source);
                RemoveMovedLink(source);
                return true;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == NativeFile.ErrorPrivilegeNotHeld)
            {
                if (Confirm(new Confirmation(ConfirmationKind.CopyLinkContents, source, "creating symbolic links needs administrator rights or Developer Mode")))
                {
                    return false;
                }

                Record(r => r.Skipped++);
                AddDone(0, 1, source);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                if (Confirm(new Confirmation(ConfirmationKind.CopyLinkContents, source, "creating symbolic links needs administrator rights or Developer Mode")))
                {
                    return false;
                }

                Record(r => r.Skipped++);
                AddDone(0, 1, source);
                return true;
            }
        }

        var created = Attempt(source, "create junction", () => NativeFile.CreateJunction(target, linkTarget));
        if (!created)
        {
            AddDone(0, 1, source);
            return true;
        }

        Record(r => r.Succeeded++);
        AddTarget(NextOrder(), target);
        AddDone(0, 1, source);
        RemoveMovedLink(source);
        return true;
    }

    private void RemoveMovedLink(string source)
    {
        if (_move)
        {
            Guard(source);
            Attempt(source, "remove link", () => Directory.Delete(source));
        }
    }
}
