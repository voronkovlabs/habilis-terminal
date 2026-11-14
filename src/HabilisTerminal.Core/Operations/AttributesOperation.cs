using HabilisTerminal.Core.Interop;

namespace HabilisTerminal.Core.Operations;

/// <summary>Ctrl+A changes (SPEC §8); null fields are left as they are (tri-state check boxes).</summary>
public sealed record AttributeChange
{
    /// <summary>Attributes to switch on (read-only, archive, hidden, system, not indexed, temporary).</summary>
    public FileAttributes Set { get; init; }

    /// <summary>Attributes to switch off.</summary>
    public FileAttributes Clear { get; init; }

    public bool? Compressed { get; init; }

    public bool? Encrypted { get; init; }

    public DateTime? CreationTimeUtc { get; init; }

    public DateTime? LastWriteTimeUtc { get; init; }

    public DateTime? LastAccessTimeUtc { get; init; }

    /// <summary>Off Windows (#274): permission bits to switch on, as Get Info and chmod do.</summary>
    public UnixFileMode ModeSet { get; init; }

    /// <summary>Off Windows: permission bits to switch off.</summary>
    public UnixFileMode ModeClear { get; init; }

    /// <summary>Apply to everything inside selected directories (links are not entered).</summary>
    public bool Recursive { get; init; }

    public static readonly FileAttributes Editable =
        FileAttributes.ReadOnly | FileAttributes.Archive | FileAttributes.Hidden | FileAttributes.System |
        FileAttributes.NotContentIndexed | FileAttributes.Temporary;
}

public sealed class AttributesOperation : FileOperation
{
    private readonly string[] _paths;
    private readonly AttributeChange _change;

    public AttributesOperation(IReadOnlyList<string> paths, AttributeChange change, IOperationHandler handler, OperationControl control)
        : base(handler, control)
    {
        if (((change.Set | change.Clear) & ~AttributeChange.Editable) != 0)
        {
            throw new ArgumentException("Only editable attributes can be set or cleared.", nameof(change));
        }

        if (change.Compressed == true && change.Encrypted == true)
        {
            throw new ArgumentException("A file cannot be both compressed and encrypted.", nameof(change));
        }

        _paths = paths.Select(Path.GetFullPath).ToArray();
        _change = change;
    }

    protected override void Execute()
    {
        var (_, items) = _change.Recursive ? Scan(_paths, followLinks: false) : (0L, _paths.Length);
        SetTotals(0, items);
        foreach (var path in _paths)
        {
            Apply(path, top: true);
        }
    }

    private void Apply(string path, bool top)
    {
        Control.Checkpoint();
        Guard(path);
        var attributes = File.GetAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var isLink = (attributes & FileAttributes.ReparsePoint) != 0;

        var applied = Attempt(path, "set attributes", () =>
        {
            // Encryption and compression change attributes themselves; do them before the plain flags.
            if (_change.Encrypted is { } encrypt && encrypt != ((attributes & FileAttributes.Encrypted) != 0) && !isLink)
            {
                Platform.Platforms.Current.FileSystem.SetEncryption(path, encrypt);
            }

            if (_change.Compressed is { } compress && compress != ((attributes & FileAttributes.Compressed) != 0) && !isLink)
            {
                NativeFile.SetCompression(path, compress);
            }

            var current = File.GetAttributes(path);
            var updated = (current & ~_change.Clear) | _change.Set;
            if (updated != current)
            {
                File.SetAttributes(path, updated & ~(FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Compressed | FileAttributes.Encrypted));
            }

            // Off Windows the permissions (#274); read-only there is only what they say.
            if (!OperatingSystem.IsWindows() && (_change.ModeSet | _change.ModeClear) != 0 && !isLink)
            {
                var mode = File.GetUnixFileMode(path);
                var wanted = (mode & ~_change.ModeClear) | _change.ModeSet;
                if (wanted != mode)
                {
                    File.SetUnixFileMode(path, wanted);
                }
            }

            // Times on read-only files need the attribute off for a moment (Windows: off Windows it would rewrite the
            // permissions).
            var readOnly = OperatingSystem.IsWindows() && (updated & FileAttributes.ReadOnly) != 0 && !isDirectory;
            if (readOnly && (_change.CreationTimeUtc ?? _change.LastWriteTimeUtc ?? _change.LastAccessTimeUtc) is not null)
            {
                File.SetAttributes(path, updated & ~FileAttributes.ReadOnly & ~FileAttributes.Directory);
            }

            try
            {
                if (_change.CreationTimeUtc is { } created)
                {
                    (isDirectory ? (Action<string, DateTime>)Directory.SetCreationTimeUtc : File.SetCreationTimeUtc)(path, created);
                }

                if (_change.LastWriteTimeUtc is { } written)
                {
                    (isDirectory ? (Action<string, DateTime>)Directory.SetLastWriteTimeUtc : File.SetLastWriteTimeUtc)(path, written);
                }

                if (_change.LastAccessTimeUtc is { } accessed)
                {
                    (isDirectory ? (Action<string, DateTime>)Directory.SetLastAccessTimeUtc : File.SetLastAccessTimeUtc)(path, accessed);
                }
            }
            finally
            {
                if (readOnly)
                {
                    File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                }
            }
        });

        if (applied && top)
        {
            Result.Succeeded++;
        }

        AddDone(0, 1, path);
        if (_change.Recursive && isDirectory && !isLink)
        {
            string[] children = [];
            Attempt(path, "read", () => children = Directory.GetFileSystemEntries(path, "*", new EnumerationOptions { AttributesToSkip = 0 }));
            foreach (var child in children)
            {
                Apply(child, top: false);
            }
        }
    }
}
