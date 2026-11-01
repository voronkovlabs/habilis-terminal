namespace HabilisTerminal.Core.Viewing;

/// <summary>F3 on a directory (SPEC §9): total size of the files below it, without entering links.</summary>
public static class DirectorySize
{
    public sealed record Result(long Bytes, int Files, int Folders, int Inaccessible);

    /// <summary>Walks the tree; <paramref name="progress"/> gets the running totals after each folder.</summary>
    public static Result Compute(string path, IProgress<Result>? progress = null, CancellationToken cancellationToken = default)
    {
        long bytes = 0;
        int files = 0, folders = 0, inaccessible = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                {
                    if (info is FileInfo file)
                    {
                        bytes += file.Length;
                        files++;
                    }
                    else
                    {
                        folders++;
                        if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                        {
                            pending.Push(info.FullName);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                inaccessible++;
            }

            progress?.Report(new Result(bytes, files, folders, inaccessible));
        }

        return new Result(bytes, files, folders, inaccessible);
    }
}
