using System.Globalization;
using HabilisTerminal.Plugins;

namespace Habilis.Archives;

/// <summary>
/// An archive shown as a folder in a panel (issue #51), as Far's arclite: Enter / Ctrl+PgDn goes in, ".." at the top
/// goes back out to the file, F5 / F6 copy or move out, F3 views through a temporary copy. An archive inside the
/// archive opens the same way from a temporary copy. Formats 7-Zip writes (zip, 7z, tar...) also take F5 / F6 in, F8
/// and F7: each change writes an updated copy next to the archive and then replaces it. Location reads like a path:
/// <c>C:\data\a.zip\docs</c>.
/// </summary>
public sealed class ArchiveProvider : IPanelProvider, IGetFiles, IPutFiles, IDeleteEntries, IMakeFolder
{
    private static readonly IReadOnlyList<PanelLayout> ArchiveLayouts =
        [new PanelLayout("Archive", [PanelColumn.Size, PanelColumn.Modified])];

    private readonly IArchiveEngine _library;
    private readonly Func<string, string?> _askPassword;
    private readonly Stack<Level> _levels = new();
    private string _folder = string.Empty;

    private ArchiveProvider(IArchiveEngine library, IArchive archive, Func<string, string?> askPassword)
    {
        _library = library;
        _askPassword = askPassword;
        _levels.Push(new Level(archive, archive.FilePath, string.Empty, TempFolder: null));
    }

    public string Title => $"{Top.Archive.Format.Name}: {Path.GetFileName(Top.DisplayPath)}";

    // The archive's file as the system writes it, then the folder inside with the system's separator (#281):
    // PluginFiles.SplitLocation in the app reads it back.
    public string Location => Top.DisplayPath + (_folder.Length > 0 ? Path.DirectorySeparatorChar + _folder.Replace('\\', Path.DirectorySeparatorChar) : string.Empty);

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
                new InfoLine("Size", Compact(tree.TotalSize, culture)),
            ];
        }
    }

    public bool CanMove => CanWrite;

    /// <summary>The archive can be changed: its format writes and it is not inside another archive.</summary>
    public bool CanWrite => _levels.Count == 1 && Top.Archive.Format.CanWrite;

    private Level Top => _levels.Peek();

    private static string PasswordPrompt(string name, bool wrong) =>
        wrong ? $"Wrong password for '{name}'. Try again:" : $"'{name}' is encrypted. Password:";

    /// <summary>"512 B", "1.2 KB", "34 MB".</summary>
    private static string Compact(long bytes, CultureInfo culture)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes.ToString(culture)} B" : $"{value.ToString(value < 10 ? "0.0" : "0", culture)} {units[unit]}";
    }

    /// <summary>
    /// Opens <paramref name="path"/> as an archive: by its extension, or with <paramref name="anyFormat"/> by
    /// signature. Null when 7z.dll is missing or no format accepts the file.
    /// </summary>
    /// <param name="askPassword">Asked for encrypted archives, with the text of the question; null cancels.</param>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static ArchiveProvider? TryOpen(string path, bool anyFormat, Func<string, string?> askPassword, IArchiveEngine? library = null)
    {
        library ??= ArchiveEngines.Default;
        var archive = library?.Open(path, anyFormat, wrong => askPassword(PasswordPrompt(Path.GetFileName(path), wrong)));
        return archive is null ? null : new ArchiveProvider(library!, archive, askPassword);
    }

    /// <summary>
    /// Opens a file and shows a place inside it (from folder history), or the top when that place is gone. Null when
    /// 7z.dll is missing or no format accepts the file.
    /// </summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static ArchiveProvider? TryOpen(string path, string innerPath, bool anyFormat, Func<string, string?> askPassword, IArchiveEngine? library = null)
    {
        if (TryOpen(path, anyFormat, askPassword, library) is not { } provider)
        {
            return null;
        }

        if (innerPath.Length > 0 && provider.Top.Tree.IsFolder(innerPath))
        {
            provider._folder = innerPath;
        }

        return provider;
    }

    /// <summary>The extensions Enter opens as folders: what is an archive to the user and 7-Zip here reads.</summary>
    public static IReadOnlyList<string> ExtensionsOpenedOnEnter(IArchiveEngine? library = null)
    {
        library ??= ArchiveEngines.Default;
        return library is null ? [] : [.. OpenedOnEnter.Where(library.Extensions.Contains)];
    }

    /// <summary>True when the extension is that of an archive (Enter tries to open such files as folders).</summary>
    public static bool HasArchiveExtension(string name, IArchiveEngine? library = null)
    {
        library ??= ArchiveEngines.Default;
        var extension = Path.GetExtension(name).TrimStart('.');
        return library is not null && extension.Length > 0 && library.Extensions.Contains(extension) && OpenedOnEnter.Contains(extension);
    }

    // A list of what is an archive to the user, not of exceptions: 7-Zip also reads documents and programs (doc, xls,
    // docx, jar, apk, exe, disk images...), and these open in their program on Enter (issue #138). Ctrl+PgDn still
    // tries any file as an archive.
    private static readonly HashSet<string> OpenedOnEnter = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "zipx", "z01", "7z", "rar", "r00", "tar", "gz", "gzip", "tgz", "tpz", "bz2", "bzip2", "tbz", "tbz2", "xz", "txz",
        "lzma", "tlz", "z", "taz", "zst", "tzst", "cab", "arj", "lzh", "lha", "cpio", "rpm", "deb", "iso", "wim", "swm", "esd", "001",
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
        var folder = Path.Combine(Path.GetTempPath(), "HabilisTerminal", "archives", Guid.NewGuid().ToString("N")[..8]);
        var copy = Path.Combine(folder, entry.Name);
        var archive = Top.Archive;
        var failures = await Task.Run(() => archive.Extract(new Dictionary<uint, string> { [item.Index] = copy }, _ => { }, _ => { }, () => _askPassword(PasswordPrompt(entry.Name, archive.PasswordWasWrong)), cancellationToken), cancellationToken);
        var nested = failures.Count == 0 && File.Exists(copy) ? await OpenOnPoolAsync(copy, anyFormat: false, wrong => _askPassword(PasswordPrompt(entry.Name, wrong))) : null;
        if (nested is null)
        {
            DeleteQuietly(folder);
            if (failures.Count > 0)
            {
                throw new PluginException($"Cannot open {entry.Name}: {failures.Values.First()}");
            }

            return false;
        }

        _levels.Push(new Level(nested, Location + Path.DirectorySeparatorChar + entry.Name, _folder, folder));
        _folder = string.Empty;
        return true;
    }

    public async Task<bool> GoUpAsync(CancellationToken cancellationToken)
    {
        if (_folder.Length > 0)
        {
            _folder = ArchiveTree.Parent(_folder);
            return true;
        }

        if (_levels.Count > 1)
        {
            var nested = _levels.Pop();
            _folder = nested.OuterFolder;
            await Task.Run(nested.Dispose, CancellationToken.None);
            return true;
        }

        return false;
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
                    emptyFolders.Add(Path.Combine(target, Local(folder[(path.Length + 1)..])));
                }

                foreach (var item in tree.ItemsUnder(path))
                {
                    targets[item.Index] = Path.Combine(target, Local(item.Path[(path.Length + 1)..]));
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
                () => _askPassword(PasswordPrompt(Path.GetFileName(archive.FilePath), archive.PasswordWasWrong)),
                cancellationToken),
            cancellationToken);
        if (started > 0)
        {
            progress.CompleteItem();
        }

        var failed = files.Count(failures.ContainsKey);
        if (request.Move && failed == 0 && skipped == 0)
        {
            // F6: the copies are complete, so the originals leave the archive.
            await DeleteAsync(request.Entries, progress, cancellationToken);
        }

        return new OperationOutcome(files.Count - failed, skipped, failed, failures.Count > 0 ? failures.Values.First() : null);
    }

    public async Task<OperationOutcome> PutFilesAsync(IReadOnlyList<string> localPaths, Func<string, Task<OverwriteChoice>> askOverwrite, IOperationProgress progress, CancellationToken cancellationToken)
    {
        EnsureWritable();
        var tree = Top.Tree;
        var add = new List<NewArchiveItem>();
        foreach (var local in localPaths)
        {
            var path = ArchiveTree.Combine(_folder, Path.GetFileName(Path.TrimEndingDirectorySeparator(local)));
            if (Directory.Exists(local))
            {
                add.Add(FolderItem(path, new DirectoryInfo(local)));
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
                foreach (var info in new DirectoryInfo(local).EnumerateFileSystemInfos("*", options))
                {
                    var inner = ArchiveTree.Combine(path, Path.GetRelativePath(local, info.FullName).Replace(Path.DirectorySeparatorChar, '\\'));
                    add.Add(info is DirectoryInfo folder ? FolderItem(inner, folder) : FileItem(inner, (FileInfo)info));
                }
            }
            else
            {
                add.Add(FileItem(path, new FileInfo(local)));
            }
        }

        // Existing files are replaced when the user agrees; existing folders simply stay.
        var removed = new HashSet<uint>();
        var skipped = 0;
        var answer = OverwriteChoice.Overwrite;
        foreach (var item in add.ToList())
        {
            if (tree.Find(item.Path) is not { } existing)
            {
                continue;
            }

            if (item.IsFolder || existing.IsFolder)
            {
                if (item.IsFolder && existing.IsFolder && existing.Item is not null)
                {
                    add.Remove(item);
                }

                continue;
            }

            if (answer is not (OverwriteChoice.OverwriteAll or OverwriteChoice.SkipAll))
            {
                answer = await askOverwrite(item.Path);
            }

            if (answer == OverwriteChoice.Cancel)
            {
                return new OperationOutcome(0, 0, 0, "Cancelled.");
            }

            if (answer is OverwriteChoice.Skip or OverwriteChoice.SkipAll)
            {
                add.Remove(item);
                skipped++;
            }
            else
            {
                removed.Add(existing.Item!.Index);
            }
        }

        var files = add.Where(i => !i.IsFolder).ToList();
        progress.SetTotals(files.Count, files.Sum(i => i.Size));
        await RewriteAsync(removed, add, progress, cancellationToken);
        return new OperationOutcome(files.Count, skipped, 0);
    }

    public async Task<OperationOutcome> DeleteAsync(IReadOnlyList<PanelEntry> entries, IOperationProgress progress, CancellationToken cancellationToken)
    {
        EnsureWritable();
        var tree = Top.Tree;
        var removed = new HashSet<uint>();
        foreach (var entry in entries)
        {
            var path = ArchiveTree.Combine(_folder, entry.Name);
            if (tree.Find(path)?.Item is { } item)
            {
                removed.Add(item.Index);
            }

            if (entry.IsFolder)
            {
                removed.UnionWith(tree.ItemsUnder(path).Select(i => i.Index));
            }
        }

        await RewriteAsync(removed, [], progress, cancellationToken);
        return new OperationOutcome(entries.Count, 0, 0);
    }

    public async Task MakeFolderAsync(string name, CancellationToken cancellationToken)
    {
        EnsureWritable();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new PluginException($"'{name}' is not a valid folder name.");
        }

        var path = ArchiveTree.Combine(_folder, name);
        if (Top.Tree.Find(path) is not null)
        {
            throw new PluginException($"'{name}' already exists.");
        }

        await RewriteAsync(new HashSet<uint>(), [new NewArchiveItem(path, null, 0, DateTime.UtcNow, FileAttributes.Directory)], NoProgress.Instance, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        var levels = _levels.ToList();
        _levels.Clear();
        await Task.Run(() => levels.ForEach(level => level.Dispose()));
    }

    // 7z.dll objects have no COM proxies, so they are created and used on thread-pool (MTA) threads only: an object
    // created on the UI thread (STA) could not be called from the pool, where extraction and updates run.

    /// <summary>A path inside the archive ('\') as a relative path of this system.</summary>
    private static string Local(string inner) => inner.Replace('\\', Path.DirectorySeparatorChar);

    private Task<IArchive?> OpenOnPoolAsync(string path, bool anyFormat, Func<bool, string?> password) =>
        Task.Run(() => _library.Open(path, anyFormat, password));

    private void EnsureWritable()
    {
        if (!CanWrite)
        {
            throw new PluginException(_levels.Count > 1
                ? "An archive inside another archive cannot be changed; copy it out first."
                : $"{Top.Archive.Format.Name} archives cannot be changed.");
        }
    }

    private static NewArchiveItem FileItem(string path, FileInfo info) =>
        new(path, info.FullName, info.Length, info.LastWriteTimeUtc, info.Attributes);

    private static NewArchiveItem FolderItem(string path, DirectoryInfo info) =>
        new(path, null, 0, info.LastWriteTimeUtc, info.Attributes);

    /// <summary>
    /// Writes the archive without <paramref name="removed"/> and with <paramref name="add"/> to a temporary file in its
    /// folder, then replaces the archive with it and reopens it. On failure or cancel the archive stays as it was.
    /// </summary>
    private async Task RewriteAsync(HashSet<uint> removed, IReadOnlyList<NewArchiveItem> add, IOperationProgress progress, CancellationToken cancellationToken)
    {
        var level = Top;
        var archive = level.Archive;
        var original = archive.FilePath;
        var keep = archive.Items.Where(i => !removed.Contains(i.Index)).Select(i => i.Index).ToList();
        var temporary = Path.Combine(Path.GetDirectoryName(original)!, $"~{Path.GetFileName(original)}.{Guid.NewGuid().ToString("N")[..8]}.tmp");
        var started = 0;
        try
        {
            await Task.Run(
                () => archive.Update(
                    temporary,
                    keep,
                    add,
                    progress.AddBytes,
                    path =>
                    {
                        if (started++ > 0)
                        {
                            progress.CompleteItem();
                        }

                        progress.Begin(path);
                    },
                    cancellationToken),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure (7-Zip, a file to add, an interop error) leaves the archive as it was and no partial copy.
            TryDelete(temporary);
            if (ex is OperationCanceledException or PluginException)
            {
                throw;
            }

            throw new PluginException(ex.Message, ex);
        }

        if (started > 0)
        {
            progress.CompleteItem();
        }

        var password = archive.Password;
        var replaced = await Task.Run(() =>
        {
            archive.Dispose();
            try
            {
                System.IO.File.Move(temporary, original, overwrite: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(temporary);
                return ex;
            }
        });
        await ReopenAsync(level, original, password);
        if (replaced is not null)
        {
            throw new PluginException($"Cannot replace {Path.GetFileName(original)}: {replaced.Message}", replaced);
        }
    }

    /// <summary>Opens the archive again after a change, in place of <paramref name="level"/>; the folder stays if it still exists.</summary>
    private async Task ReopenAsync(Level level, string path, string? password)
    {
        _levels.Pop();
        var archive = await OpenOnPoolAsync(path, anyFormat: true, wrong => (wrong ? null : password) ?? _askPassword(PasswordPrompt(Path.GetFileName(path), wrong)))
            ?? throw new PluginException($"{Path.GetFileName(path)} cannot be opened after the change.");
        var reopened = new Level(archive, level.DisplayPath, level.OuterFolder, level.TempFolder);
        _levels.Push(reopened);
        while (_folder.Length > 0 && !reopened.Tree.IsFolder(_folder))
        {
            _folder = ArchiveTree.Parent(_folder);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            System.IO.File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary file; left behind.
        }
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

    private sealed class NoProgress : IOperationProgress
    {
        public static NoProgress Instance { get; } = new();

        public void SetTotals(long? items, long? bytes)
        {
        }

        public void Begin(string item)
        {
        }

        public void AddBytes(long bytes)
        {
        }

        public void CompleteItem()
        {
        }
    }

    /// <summary>One open archive: the outer one, or one inside it opened from <see cref="TempFolder"/>.</summary>
    private sealed record Level(IArchive Archive, string DisplayPath, string OuterFolder, string? TempFolder) : IDisposable
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
