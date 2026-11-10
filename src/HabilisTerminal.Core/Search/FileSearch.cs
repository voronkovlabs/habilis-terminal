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
public sealed record FileSearchRequest(FileMask Mask, string? Text, bool MatchCase, bool WholeWords, bool Folders, IReadOnlyList<string> Roots, bool Recursive);

public sealed record FoundItem(string Path, bool IsFolder);

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
    public static int Run(FileSearchRequest request, Action<FoundItem> found, Action<string>? folderStarted = null, CancellationToken cancellationToken = default)
    {
        var finder = string.IsNullOrEmpty(request.Text) ? null : new TextFinder(request.Text, request.MatchCase, request.WholeWords);
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
                else if (request.Mask.IsMatch(entry.Name) && (finder is null || finder.IsIn(path, cancellationToken)))
                {
                    found(new FoundItem(path, IsFolder: false));
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
