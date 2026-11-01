namespace HabilisTerminal.Core.Viewing;

/// <summary>F3 on a directory (SPEC §9): total size of the files below it, without entering links.</summary>
public static class DirectorySize
{
    public sealed record Result(long Bytes, int Files, int Folders, int Inaccessible);

    /// <summary>Walks the tree; <paramref name="progress"/> gets the running totals after each folder.</summary>
    public static Result Compute(string path, IProgress<Result>? progress = null, CancellationToken cancellationToken = default)
    {
        var walk = new Walk(path);
        walk.Run(progress, cancellationToken: cancellationToken);
        return walk.Totals;
    }

    /// <summary>
    /// A count that can stop between folders and continue later from where it stopped: it keeps the folders still
    /// to read and the totals so far. Not thread-safe; one thread runs it at a time.
    /// </summary>
    public sealed class Walk
    {
        private static readonly EnumerationOptions Options = new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };

        private readonly Stack<string> _pending = new();
        private long _bytes;
        private int _files, _folders, _inaccessible;

        public Walk(string path) => _pending.Push(path);

        public bool IsDone => _pending.Count == 0;

        public Result Totals => new(_bytes, _files, _folders, _inaccessible);

        /// <summary>
        /// Reads folders until the tree is done (true) or <paramref name="pause"/>, asked after each folder, says to
        /// stop (false; <see cref="Run"/> again continues). Cancellation throws and leaves the walk where it was.
        /// </summary>
        public bool Run(IProgress<Result>? progress = null, Func<bool>? pause = null, CancellationToken cancellationToken = default)
        {
            while (_pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadNext();
                progress?.Report(Totals);
                if (_pending.Count > 0 && pause?.Invoke() == true)
                {
                    return false;
                }
            }

            return true;
        }

        private void ReadNext()
        {
            var directory = _pending.Pop();
            try
            {
                foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", Options))
                {
                    if (info is FileInfo file)
                    {
                        _bytes += file.Length;
                        _files++;
                    }
                    else
                    {
                        _folders++;
                        if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                        {
                            _pending.Push(info.FullName);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _inaccessible++;
            }
        }
    }
}
