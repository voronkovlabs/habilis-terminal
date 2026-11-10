using HabilisTerminal.Core.FileSystem;

namespace HabilisTerminal.Core.Search;

/// <summary>Where Alt+F7 looks, as in Far.</summary>
public enum SearchScope
{
    FromCurrentFolder,
    CurrentFolderOnly,
    SelectedFolders,
    FromDriveRoot,
    AllLocalDrives,
}

/// <param name="Text">Text the files must hold; null or empty finds by name only.</param>
/// <param name="Folders">Folders whose names match are found too, when no text is asked for.</param>
public sealed record FileSearchRequest(FileMask Mask, string? Text, bool MatchCase, bool WholeWords, bool Folders, IReadOnlyList<string> Roots, bool Recursive)
{
    /// <summary><see cref="Text"/> is a regular expression, matched within a line (issue #209).</summary>
    public bool Regex { get; init; }

    /// <summary>Look inside files that plugins open as folders (issue #210).</summary>
    public bool Archives { get; init; }
}

/// <param name="Path">The full path; inside an archive, the archive's path, a backslash and the entry's inner path.</param>
public sealed record FoundItem(string Path, bool IsFolder)
{
    /// <summary>The archive this entry is inside, or null for a file on disk.</summary>
    public string? Container { get; init; }

    /// <summary>The path inside <see cref="Container"/>, with backslashes.</summary>
    public string? InnerPath { get; init; }
}

/// <summary>An entry inside a file a plugin opens as a folder, as the plugin gives it to the search (issue #210).</summary>
/// <param name="InnerPath">Below the archive's top, folders separated by backslashes.</param>
/// <param name="OpenAsync">The entry's content, read before the next entry is asked for; null when it cannot be read.</param>
public sealed record ContainerEntry(string InnerPath, bool IsFolder, long? Size, DateTime? ModifiedUtc, Func<CancellationToken, Task<Stream>>? OpenAsync);

/// <summary>Files the search can look inside: the plugins that open them (issue #210).</summary>
public interface IContainerSearch
{
    /// <summary>Some plugin may search inside a file with this name.</summary>
    bool Claims(string fileName);

    /// <summary>The entries a plugin gives the search, or null when no plugin searches this file.</summary>
    IAsyncEnumerable<ContainerEntry>? Enumerate(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Alt+F7 (issue #198): walks the roots and reports what matches as it goes. Links are not entered, as in folder sizes,
/// so a junction loop or a link to a whole drive cannot make it run forever. A folder that cannot be read is skipped.
/// </summary>
public static class FileSearch
{
    public static IReadOnlyList<string> RootsFor(SearchScope scope, string currentFolder, IReadOnlyList<string> selectedFolders) => scope switch
    {
        SearchScope.SelectedFolders => selectedFolders.Count > 0 ? selectedFolders : [currentFolder],
        SearchScope.FromDriveRoot => [Path.GetPathRoot(currentFolder) is { Length: > 0 } root ? root : currentFolder],
        SearchScope.AllLocalDrives => [.. DriveInfo.GetDrives().Where(IsLocal).Select(d => d.RootDirectory.FullName)],
        _ => [currentFolder],
    };

    /// <param name="found">Each match, on the searching thread.</param>
    /// <param name="folderStarted">Each folder as it is read, for a progress line.</param>
    /// <returns>How many folders could not be read.</returns>
    public static int Run(FileSearchRequest request, Action<FoundItem> found, Action<string>? folderStarted = null, CancellationToken cancellationToken = default) =>
        Run(request, found, folderStarted, cache: null, containers: null, cancellationToken);

    /// <param name="found">Each match, on the searching thread.</param>
    /// <param name="folderStarted">Each folder or archive as it is read, for a progress line.</param>
    /// <param name="cache">Answers of the same text search before (issue #211); null reads every file.</param>
    /// <param name="containers">Who looks inside archives, used when <see cref="FileSearchRequest.Archives"/> is set.</param>
    /// <returns>How many folders and archives could not be read.</returns>
    public static int Run(FileSearchRequest request, Action<FoundItem> found, Action<string>? folderStarted, SearchCache? cache, IContainerSearch? containers,
        CancellationToken cancellationToken)
    {
        var finder = string.IsNullOrEmpty(request.Text) ? null : new TextFinder(request.Text, request.MatchCase, request.WholeWords, request.Regex);
        if (finder is null)
        {
            cache = null;
        }

        containers = request.Archives ? containers : null;
        var unreadable = 0;
        var pending = new Stack<string>();
        foreach (var root in request.Roots.Reverse())
        {
            pending.Push(root);
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            folderStarted?.Invoke(folder);
            FileEntry[] entries;
            try
            {
                entries = DirectoryReader.ReadAll(folder, cancellationToken);
            }
            catch (DirectoryReadException)
            {
                unreadable++;
                continue;
            }

            var subfolders = new List<string>();
            foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(folder, entry.Name);
                if (entry.IsDirectory)
                {
                    if (request.Recursive && !entry.IsLink)
                    {
                        subfolders.Add(path);
                    }

                    if (request.Folders && finder is null && request.Mask.IsMatch(entry.Name))
                    {
                        found(new FoundItem(path, IsFolder: true));
                    }
                }
                else
                {
                    // By name an archive comes before its entries. With text, an archive a plugin looked inside is not
                    // searched as a file: its packed bytes say nothing.
                    var nameMatches = request.Mask.IsMatch(entry.Name);
                    if (finder is null && nameMatches)
                    {
                        found(new FoundItem(path, IsFolder: false));
                    }

                    var searched = containers is not null && containers.Claims(entry.Name)
                        && SearchContainer(request, finder, cache, containers, path, found, folderStarted, ref unreadable, cancellationToken);
                    if (finder is not null && nameMatches && !searched
                        && Holds(cache, path, entry.Length, entry.LastWriteTimeUtc, () => finder.IsIn(path, cancellationToken)))
                    {
                        found(new FoundItem(path, IsFolder: false));
                    }
                }
            }

            // In name order, as a listing shows them.
            for (var i = subfolders.Count - 1; i >= 0; i--)
            {
                pending.Push(subfolders[i]);
            }
        }

        return unreadable;
    }

    /// <summary>The text check through the cache: a file of the same size and date is answered from it.</summary>
    private static bool Holds(SearchCache? cache, string key, long size, DateTime modifiedUtc, Func<bool> read)
    {
        if (cache?.Lookup(key, size, modifiedUtc) is { } known)
        {
            return known;
        }

        var result = read();
        cache?.Record(key, size, modifiedUtc, result);
        return result;
    }

    /// <summary>The entries a plugin gives for an archive; false when no plugin searches it. A failing plugin counts as unreadable.</summary>
    private static bool SearchContainer(FileSearchRequest request, TextFinder? finder, SearchCache? cache, IContainerSearch containers, string path,
        Action<FoundItem> found, Action<string>? folderStarted, ref int unreadable, CancellationToken cancellationToken)
    {
        IAsyncEnumerable<ContainerEntry>? entries;
        try
        {
            entries = containers.Enumerate(path, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            unreadable++;
            return false;
        }

        if (entries is null)
        {
            return false;
        }

        folderStarted?.Invoke(path);
        try
        {
            foreach (var entry in entries.ToBlockingEnumerable(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inner = entry.InnerPath.Replace('/', '\\').Trim('\\');
                if (inner.Length == 0 || !request.Mask.IsMatch(Path.GetFileName(inner)))
                {
                    continue;
                }

                var item = new FoundItem(path + "\\" + inner, entry.IsFolder) { Container = path, InnerPath = inner };
                if (entry.IsFolder)
                {
                    if (request.Folders && finder is null)
                    {
                        found(item);
                    }

                    continue;
                }

                if (finder is null)
                {
                    found(item);
                    continue;
                }

                if (entry.OpenAsync is not { } open)
                {
                    continue;
                }

                bool Read()
                {
                    using var stream = open(cancellationToken).GetAwaiter().GetResult();
                    return finder.IsInContent(stream, cancellationToken);
                }

                var holds = entry.Size is { } size && entry.ModifiedUtc is { } modified
                    ? Holds(cache, item.Path, size, modified, Read)
                    : Read();
                if (holds)
                {
                    found(item);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // A damaged archive or a failing plugin: what was found stays found.
            unreadable++;
        }

        return true;
    }

    private static bool IsLocal(DriveInfo drive)
    {
        try
        {
            return drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
