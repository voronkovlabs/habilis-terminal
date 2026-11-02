using System.Globalization;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Plugins;

namespace HabilisTerminal.Core.Archives;

/// <summary>
/// An archive shown as a folder in a panel (issue #51), as Far's arclite: Enter / Ctrl+PgDn goes in, ".." at the top
/// goes back out to the file, F5 copies out, F3 views through a temporary copy. An archive inside the archive opens
/// the same way from a temporary copy. Location reads like a path: <c>C:\data\a.zip\docs</c>.
/// </summary>
public sealed class ArchiveProvider : IPanelProvider, IGetFiles
{
    private static readonly IReadOnlyList<PanelLayout> ArchiveLayouts =
        [new PanelLayout("Archive", [PanelColumn.Size, PanelColumn.Modified])];

    private readonly SevenZipLibrary _library;
    private readonly Func<string, string?> _askPassword;
    private readonly Stack<Level> _levels = new();
    private string _folder = string.Empty;

    private ArchiveProvider(SevenZipLibrary library, SevenZipArchive archive, Func<string, string?> askPassword)
    {
        _library = library;
        _askPassword = askPassword;
        _levels.Push(new Level(archive, archive.FilePath, string.Empty, TempFolder: null));
    }

    public string Title => $"{Top.Archive.Format.Name}: {Path.GetFileName(Top.DisplayPath)}";

    public string Location => Top.DisplayPath + (_folder.Length > 0 ? "\\" + _folder : string.Empty);

    public IReadOnlyList<PanelLayout> Layouts => ArchiveLayouts;

    public IReadOnlyList<PanelSortKey> SortKeys => [];

    public IReadOnlyList<InfoLine> Info
    {
        get
        {
            var culture = CultureInfo.CurrentCulture;
            var tree = Top.Tree;
            return
            [
                new InfoLine("Archive", Top.DisplayPath),
                new InfoLine("Format", Top.Archive.Format.Name),
                new InfoLine("Files", tree.FileCount.ToString("N0", culture)),
                new InfoLine("Folders", tree.FolderCount.ToString("N0", culture)),
                new InfoLine("Size", SizeFormat.Compact(tree.TotalSize, culture)),
            ];
        }
    }

    public bool CanMove => false;

    private Level Top => _levels.Peek();

    /// <summary>
    /// Opens <paramref name="path"/> as an archive: by its extension, or with <paramref name="anyFormat"/> by
    /// signature. Null when 7z.dll is missing or no format accepts the file.
    /// </summary>
    /// <param name="askPassword">Asked (with the archive name) for encrypted archives; null cancels.</param>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static ArchiveProvider? TryOpen(string path, bool anyFormat, Func<string, string?> askPassword, SevenZipLibrary? library = null)
    {
        library ??= SevenZipLibrary.Default;
        var archive = library?.Open(path, anyFormat, () => askPassword(Path.GetFileName(path)));
        return archive is null ? null : new ArchiveProvider(library!, archive, askPassword);
    }

    /// <summary>
    /// A location from folder history (<c>C:\data\a.zip\docs</c>): the longest existing file is the archive, the
    /// rest a folder inside it. Null when no part is an archive.
    /// </summary>
    public static ArchiveProvider? TryOpenLocation(string location, Func<string, string?> askPassword, SevenZipLibrary? library = null)
    {
        var file = location.TrimEnd('\\');
        while (file.Length > 0 && !File.Exists(file))
        {
            var cut = file.LastIndexOf('\\');
            if (cut <= 2)
            {
                return null;
            }

            file = file[..cut];
        }

        if (file.Length == 0 || TryOpen(file, anyFormat: true, askPassword, library) is not { } provider)
        {
            return null;
        }

        var inner = location.Length > file.Length ? location[file.Length..].Trim('\\') : string.Empty;
        if (provider.Top.Tree.IsFolder(inner))
        {
            provider._folder = inner;
        }

        return provider;
    }

    /// <summary>True when the extension belongs to a format 7z.dll reads (Enter tries to open such files).</summary>
    public static bool HasArchiveExtension(string name, SevenZipLibrary? library = null)
    {
        library ??= SevenZipLibrary.Default;
        var extension = Path.GetExtension(name).TrimStart('.');
        return library is not null && extension.Length > 0 && library.Extensions.Contains(extension) && !NotOpenedOnEnter.Contains(extension);
    }

    // Formats 7-Zip reads but that are documents or programs to the user: Enter opens them in their program and
    // Ctrl+PgDn still opens them as archives.
    private static readonly HashSet<string> NotOpenedOnEnter = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "dll", "sys", "msi", "msp", "docx", "xlsx", "pptx", "odt", "ods", "odp", "epub", "chm", "hfs", "dmg",
        "elf", "macho", "so", "img", "vhd", "vhdx", "vmdk", "vdi", "qcow", "qcow2", "mbr", "gpt", "fat", "ntfs", "lib", "a",
    };

    public async IAsyncEnumerable<IReadOnlyList<PanelEntry>> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return Top.Tree.List(_folder)
            .Select(n => new PanelEntry(n.Name, n.IsFolder, n.IsFolder ? null : n.Size, n.ModifiedUtc))
            .ToList();
    }

    public async Task<bool> EnterAsync(PanelEntry entry, CancellationToken cancellationToken)
    {
        var path = ArchiveTree.Combine(_folder, entry.Name);
        if (entry.IsFolder)
        {
            if (!Top.Tree.IsFolder(path))
            {
                return false;
            }

            _folder = path;
            return true;
        }

        if (!HasArchiveExtension(entry.Name, _library) || Top.Tree.Find(path)?.Item is not { } item)
        {
            return false;
        }

        // An archive inside: open it from a temporary copy.
        var folder = Path.Combine(Path.GetTempPath(), Settings.AppPaths.ProductFolder, "archives", Guid.NewGuid().ToString("N")[..8]);
        var copy = Path.Combine(folder, entry.Name);
        var archive = Top.Archive;
        var failures = await Task.Run(() => archive.Extract(new Dictionary<uint, string> { [item.Index] = copy }, _ => { }, _ => { }, () => _askPassword(entry.Name), cancellationToken), cancellationToken);
        var nested = failures.Count == 0 && File.Exists(copy) ? _library.Open(copy, anyFormat: false, () => _askPassword(entry.Name)) : null;
        if (nested is null)
        {
            DeleteQuietly(folder);
            if (failures.Count > 0)
            {
                throw new PluginException($"Cannot open {entry.Name}: {failures.Values.First()}");
            }

            return false;
        }

        _levels.Push(new Level(nested, Location + "\\" + entry.Name, _folder, folder));
        _folder = string.Empty;
        return true;
    }

    public Task<bool> GoUpAsync(CancellationToken cancellationToken)
    {
        if (_folder.Length > 0)
        {
            _folder = ArchiveTree.Parent(_folder);
            return Task.FromResult(true);
        }

        if (_levels.Count > 1)
        {
            var nested = _levels.Pop();
            nested.Dispose();
            _folder = nested.OuterFolder;
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public async Task<OperationOutcome> GetFilesAsync(TransferRequest request, IOperationProgress progress, CancellationToken cancellationToken)
    {
        var tree = Top.Tree;
        var targets = new Dictionary<uint, string>();
        var emptyFolders = new List<string>();
        foreach (var entry in request.Entries)
        {
            var path = ArchiveTree.Combine(_folder, entry.Name);
            var target = Path.Combine(request.LocalFolder, entry.Name);
            if (entry.IsFolder)
            {
                emptyFolders.Add(target);
                foreach (var folder in tree.FoldersUnder(path))
                {
                    emptyFolders.Add(Path.Combine(target, folder[(path.Length + 1)..]));
                }

                foreach (var item in tree.ItemsUnder(path))
                {
                    targets[item.Index] = Path.Combine(target, item.Path[(path.Length + 1)..]);
                }
            }
            else if (tree.Find(path)?.Item is { } item)
            {
                targets[item.Index] = target;
            }
        }

        var items = Top.Archive.Items;
        var skipped = 0;
        var answer = OverwriteChoice.Overwrite;
        foreach (var (index, target) in targets.Where(t => !items[(int)t.Key].IsFolder && File.Exists(t.Value)).ToList())
        {
            if (answer is not (OverwriteChoice.OverwriteAll or OverwriteChoice.SkipAll))
            {
                answer = await request.AskOverwrite(target);
            }

            if (answer == OverwriteChoice.Cancel)
            {
                return new OperationOutcome(0, 0, 0, "Cancelled.");
            }

            if (answer is OverwriteChoice.Skip or OverwriteChoice.SkipAll)
            {
                targets.Remove(index);
                skipped++;
            }
        }

        var files = targets.Keys.Where(i => !items[(int)i].IsFolder).ToList();
        progress.SetTotals(files.Count, files.Sum(i => items[(int)i].Size));
        foreach (var folder in emptyFolders)
        {
            Directory.CreateDirectory(folder);
        }

        var archive = Top.Archive;
        var started = 0;
        var failures = await Task.Run(
            () => archive.Extract(
                targets,
                progress.AddBytes,
                item =>
                {
                    if (item.IsFolder)
                    {
                        return;
                    }

                    if (started++ > 0)
                    {
                        progress.CompleteItem();
                    }

                    progress.Begin(item.Path);
                },
                () => _askPassword(Path.GetFileName(archive.FilePath)),
                cancellationToken),
            cancellationToken);
        if (started > 0)
        {
            progress.CompleteItem();
        }

        var failed = files.Count(failures.ContainsKey);
        return new OperationOutcome(files.Count - failed, skipped, failed, failures.Count > 0 ? failures.Values.First() : null);
    }

    public ValueTask DisposeAsync()
    {
        while (_levels.Count > 0)
        {
            _levels.Pop().Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static void DeleteQuietly(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary folder; left for the system to clean.
        }
    }

    /// <summary>One open archive: the outer one, or one inside it opened from <see cref="TempFolder"/>.</summary>
    private sealed record Level(SevenZipArchive Archive, string DisplayPath, string OuterFolder, string? TempFolder) : IDisposable
    {
        public ArchiveTree Tree { get; } = new(Archive.Items);

        public void Dispose()
        {
            Archive.Dispose();
            if (TempFolder is not null)
            {
                DeleteQuietly(TempFolder);
            }
        }
    }
}
