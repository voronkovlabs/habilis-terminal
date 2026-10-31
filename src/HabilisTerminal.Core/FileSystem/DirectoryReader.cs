using System.IO.Enumeration;
using System.Security;

namespace HabilisTerminal.Core.FileSystem;

/// <summary>Why a directory could not be read.</summary>
public enum DirectoryReadError
{
    NotFound,
    AccessDenied,
    IoError,
}

public sealed class DirectoryReadException(string path, DirectoryReadError error, Exception inner)
    : IOException($"Cannot read '{path}': {inner.Message}", inner)
{
    public string Path { get; } = path;

    public DirectoryReadError Error { get; } = error;
}

/// <summary>
/// Reads directories with one <c>NtQueryDirectoryFile</c> pass (via <see cref="FileSystemEnumerable{TResult}"/>):
/// name, attributes, size and times come with the listing itself (SPEC §11).
/// </summary>
public static class DirectoryReader
{
    public const int DefaultFirstBatchSize = 256;
    public const int DefaultBatchSize = 4096;

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
        BufferSize = 64 * 1024,
    };

    /// <summary>
    /// Enumerates the directory in batches: a small first batch so the panel can draw immediately,
    /// then larger ones. Runs synchronously on the calling thread; call it from a background thread.
    /// </summary>
    public static IEnumerable<FileEntry[]> ReadBatches(
        string path,
        int firstBatchSize = DefaultFirstBatchSize,
        int batchSize = DefaultBatchSize,
        CancellationToken cancellationToken = default)
    {
        IEnumerator<FileEntry> enumerator;
        try
        {
            enumerator = CreateEnumerable(path).GetEnumerator();
        }
        catch (Exception ex) when (Classify(ex) is { } error)
        {
            throw new DirectoryReadException(path, error, ex);
        }

        using (enumerator)
        {
            var batch = new List<FileEntry>(firstBatchSize);
            var limit = firstBatchSize;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool hasNext;
                try
                {
                    hasNext = enumerator.MoveNext();
                }
                catch (Exception ex) when (Classify(ex) is { } error)
                {
                    throw new DirectoryReadException(path, error, ex);
                }

                if (!hasNext)
                {
                    break;
                }

                batch.Add(enumerator.Current);
                if (batch.Count >= limit)
                {
                    yield return batch.ToArray();
                    batch.Clear();
                    limit = batchSize;
                }
            }

            if (batch.Count > 0)
            {
                yield return batch.ToArray();
            }
        }
    }

    /// <summary>Reads the whole directory into one array (enumeration order).</summary>
    public static FileEntry[] ReadAll(string path, CancellationToken cancellationToken = default)
    {
        var all = new List<FileEntry>();
        foreach (var batch in ReadBatches(path, DefaultBatchSize, DefaultBatchSize, cancellationToken))
        {
            all.AddRange(batch);
        }

        return all.ToArray();
    }

    /// <summary>Reads one entry by name, or null when it no longer exists (used to apply watcher changes).</summary>
    public static FileEntry? ReadEntry(string directory, string name)
    {
        var info = new FileInfo(System.IO.Path.Combine(directory, name));
        try
        {
            info.Refresh();
            if (!info.Exists && !Directory.Exists(info.FullName))
            {
                return null;
            }

            FileSystemInfo item = (info.Attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(info.FullName) : info;
            return new FileEntry(
                name,
                item.Attributes,
                item is FileInfo file ? file.Length : 0,
                item.CreationTimeUtc,
                item.LastWriteTimeUtc,
                item.LastAccessTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileSystemEnumerable<FileEntry> CreateEnumerable(string path) =>
        new(path, static (ref FileSystemEntry entry) => new FileEntry(
            entry.FileName.ToString(),
            entry.Attributes,
            entry.IsDirectory ? 0 : entry.Length,
            entry.CreationTimeUtc.UtcDateTime,
            entry.LastWriteTimeUtc.UtcDateTime,
            entry.LastAccessTimeUtc.UtcDateTime), Options);

    private static DirectoryReadError? Classify(Exception ex) => ex switch
    {
        DirectoryNotFoundException or FileNotFoundException => DirectoryReadError.NotFound,
        UnauthorizedAccessException or SecurityException => DirectoryReadError.AccessDenied,
        IOException => DirectoryReadError.IoError,
        _ => null,
    };
}
