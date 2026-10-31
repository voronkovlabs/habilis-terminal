namespace HabilisTerminal.Core.FileSystem;

/// <summary>
/// One directory entry as returned by a single enumeration pass (no extra calls per file).
/// A compact value type: a 100k-entry listing is one array.
/// </summary>
public readonly record struct FileEntry(
    string Name,
    FileAttributes Attributes,
    long Length,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc,
    DateTime LastAccessTimeUtc)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;

    public bool IsSystem => (Attributes & FileAttributes.System) != 0;

    public bool IsReadOnly => (Attributes & FileAttributes.ReadOnly) != 0;

    /// <summary>Symbolic link, junction or another reparse point; never traversed implicitly.</summary>
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>Extension without the dot, as Far shows it; empty for directories and names without one.</summary>
    public ReadOnlySpan<char> Extension => IsDirectory ? default : ExtensionOf(Name);

    public static ReadOnlySpan<char> ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? default : name.AsSpan(dot + 1);
    }
}
