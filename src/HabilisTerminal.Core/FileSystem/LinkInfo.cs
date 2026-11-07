using System.Text;
using HabilisTerminal.Core.Interop;

namespace HabilisTerminal.Core.FileSystem;

/// <summary>What kind of link an entry is. A reparse point that holds its own data, such as a cloud folder, is none of these.</summary>
public enum LinkKind : byte
{
    None,
    Junction,
    MountPoint,
    Symlink,
    WslLink,
    AppAlias,
    Other,
}

/// <summary>Everything known about a link or a file with several names, for the attributes dialog (issue #178).</summary>
public sealed record LinkInfo(
    LinkKind Kind,
    uint Tag,
    string? StoredTarget,
    bool IsRelative,
    string? FullTarget,
    string? FinalTarget,
    bool? TargetExists,
    bool? TargetIsFolder,
    int HardLinks,
    IReadOnlyList<string> OtherNames)
{
    private const uint NameSurrogate = 0x20000000;
    private const uint TagAppExecLink = 0x8000001B;
    private const uint TagLxSymlink = 0xA000001D;
    private const string VolumePrefix = @"\\?\Volume{";
    private const string NtVolumePrefix = @"\??\Volume{";

    /// <summary>The word for the panels' size column and the dialog.</summary>
    public static string Name(LinkKind kind) => kind switch
    {
        LinkKind.Junction => "Junction",
        LinkKind.MountPoint => "Mount point",
        LinkKind.Symlink => "Symlink",
        LinkKind.WslLink => "WSL link",
        LinkKind.AppAlias => "App alias",
        LinkKind.Other => "Link",
        _ => string.Empty,
    };

    /// <summary>
    /// By the reparse tag: Windows marks a tag that names something elsewhere with one bit. App aliases do not have that
    /// bit but lead to a program. Cloud and deduplication tags hold data here, so they are no links.
    /// </summary>
    public static LinkKind KindOf(uint tag) => tag switch
    {
        0 => LinkKind.None,
        NativeFile.ReparseTagMountPoint => LinkKind.Junction,
        NativeFile.ReparseTagSymlink => LinkKind.Symlink,
        TagLxSymlink => LinkKind.WslLink,
        TagAppExecLink => LinkKind.AppAlias,
        _ => (tag & NameSurrogate) != 0 ? LinkKind.Other : LinkKind.None,
    };

    /// <summary>The kind of an entry known to be a reparse point. A junction is read further, as a mount point differs only by its target.</summary>
    public static LinkKind KindOfPath(string path)
    {
        var kind = KindOf(NativeFile.ReparseTag(path));
        return kind == LinkKind.Junction && NativeFile.ReadReparseData(path) is { } data && IsVolume(Parse(data).Target)
            ? LinkKind.MountPoint
            : kind;
    }

    /// <summary>What a reparse buffer says: its tag, its target as stored, and whether that is relative. No target when the buffer is of an unknown kind.</summary>
    public static (uint Tag, string? Target, bool IsRelative) Parse(byte[] data)
    {
        if (data.Length < 8)
        {
            return (0, null, false);
        }

        var tag = BitConverter.ToUInt32(data, 0);
        return tag switch
        {
            NativeFile.ReparseTagMountPoint => (tag, PathFrom(data, 16), false),
            NativeFile.ReparseTagSymlink => (tag, PathFrom(data, 20), data.Length >= 20 && (BitConverter.ToUInt32(data, 16) & 1) != 0),
            TagLxSymlink when data.Length > 12 => (tag, Encoding.UTF8.GetString(data, 12, Math.Min(BitConverter.ToUInt16(data, 4) - 4, data.Length - 12)), false),
            TagAppExecLink when data.Length > 12 => (tag, AliasTarget(data), false),
            _ => (tag, null, false),
        };
    }

    /// <summary>Null when the path is neither a link nor a file with several names.</summary>
    public static LinkInfo? Read(string path)
    {
        var hardLinks = NativeFile.HardLinkCount(path);
        var others = hardLinks > 1 ? NativeFile.HardLinkNames(path).Where(n => !string.Equals(n, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)).ToList() : [];
        var tag = NativeFile.ReparseTag(path);
        var kind = KindOf(tag);
        if (kind == LinkKind.None)
        {
            return hardLinks > 1 ? new LinkInfo(LinkKind.None, tag, null, false, null, null, null, null, hardLinks, others) : null;
        }

        var (_, stored, relative) = NativeFile.ReadReparseData(path) is { } data ? Parse(data) : default;
        if (IsVolume(stored))
        {
            kind = LinkKind.MountPoint;
        }

        stored = Readable(stored);
        var full = FullTargetOf(path, stored, kind);
        var final = FinalTargetOf(path)?.FullName;
        var end = final ?? full;
        var isFolder = end is not null && Directory.Exists(end);

        // A WSL path means nothing to Windows, and the WindowsApps folder an alias leads to is closed to users.
        bool? exists = kind is LinkKind.WslLink or LinkKind.AppAlias ? null : isFolder || (end is not null && File.Exists(end));
        return new LinkInfo(
            kind,
            tag,
            stored,
            relative,
            full,
            string.Equals(final?.TrimEnd('\\'), full?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? null : final,
            exists,
            exists == true ? isFolder : null,
            hardLinks,
            others);
    }

    private static bool IsVolume(string? stored) =>
        stored?.StartsWith(NtVolumePrefix, StringComparison.OrdinalIgnoreCase) == true || stored?.StartsWith(VolumePrefix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Without the kernel's prefix: "\??\C:\x" is C:\x, "\??\UNC\s\x" is \\s\x.</summary>
    private static string? Readable(string? stored)
    {
        if (stored is null || !stored.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return stored;
        }

        var rest = stored[4..];
        if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + rest[4..];
        }

        return rest.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase) ? @"\\?\" + rest : rest.TrimEnd('\\') is { Length: > 2 } trimmed ? trimmed : rest;
    }

    private static string? FullTargetOf(string path, string? stored, LinkKind kind)
    {
        if (stored is null || kind == LinkKind.WslLink)
        {
            return null;
        }

        if (stored.StartsWith(VolumePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return stored;
        }

        try
        {
            return Path.IsPathFullyQualified(stored) ? stored : Path.GetFullPath(stored, Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static FileSystemInfo? FinalTargetOf(string path)
    {
        try
        {
            return (Directory.Exists(path) ? Directory.ResolveLinkTarget(path, returnFinalTarget: true) : File.ResolveLinkTarget(path, returnFinalTarget: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Print name, or substitute name when there is none. The buffer holds offsets from <paramref name="pathBuffer"/>.</summary>
    private static string? PathFrom(byte[] data, int pathBuffer)
    {
        if (data.Length < pathBuffer)
        {
            return null;
        }

        var substitute = Slice(data, pathBuffer + BitConverter.ToUInt16(data, 8), BitConverter.ToUInt16(data, 10));
        var print = Slice(data, pathBuffer + BitConverter.ToUInt16(data, 12), BitConverter.ToUInt16(data, 14));

        // A mount point of a volume has no print name, so the substitute name tells what it is.
        return string.IsNullOrEmpty(print) || substitute?.StartsWith(NtVolumePrefix, StringComparison.OrdinalIgnoreCase) == true ? substitute : print;
    }

    private static string? Slice(byte[] data, int offset, int length) =>
        offset >= 0 && offset + length <= data.Length ? Encoding.Unicode.GetString(data, offset, length) : null;

    /// <summary>The buffer holds package, app and target, each ending in a zero: the third is the program.</summary>
    private static string? AliasTarget(byte[] data)
    {
        var parts = Encoding.Unicode.GetString(data, 12, data.Length - 12).Split('\0');
        return parts.Length >= 3 && parts[2].Length > 0 ? parts[2] : null;
    }
}
