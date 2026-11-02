using System.Runtime.InteropServices;

namespace HabilisTerminal.Core.Archives;

/// <summary>One item of an archive as 7-Zip lists it; <see cref="Path"/> uses '\' and has no leading separator.</summary>
public sealed record ArchiveItem(uint Index, string Path, bool IsFolder, long Size, DateTime? ModifiedUtc, bool Encrypted);

/// <summary>An open archive: its items and extraction. Not thread-safe; one operation at a time.</summary>
public sealed class SevenZipArchive : IDisposable
{
    private readonly IInArchive _archive;
    private readonly InStream _input;
    private string? _password;

    internal SevenZipArchive(string path, ArchiveFormat format, IInArchive archive, InStream input, string? password)
    {
        FilePath = path;
        Format = format;
        _archive = archive;
        _input = input;
        _password = password;
        Items = ReadItems();
    }

    public string FilePath { get; }

    public ArchiveFormat Format { get; }

    public IReadOnlyList<ArchiveItem> Items { get; }

    public void Dispose()
    {
        SevenZipLibrary.Release(_archive);
        _input.Dispose();
    }

    /// <summary>
    /// Extracts <paramref name="targets"/> (item index → file path; folders are created). A failed item leaves no
    /// partial file. Returns the indices that failed with the reason.
    /// </summary>
    /// <param name="password">Asked for encrypted items when no password is known yet; null skips them.</param>
    /// <exception cref="OperationCanceledException">Cancelled; the item in progress is removed.</exception>
    public IReadOnlyDictionary<uint, string> Extract(
        IReadOnlyDictionary<uint, string> targets, Action<long> bytesWritten, Action<ArchiveItem> itemStarted, Func<string?>? password, CancellationToken cancellationToken)
    {
        var indices = targets.Keys.Order().ToArray();
        using var callback = new ExtractCallback(this, targets, bytesWritten, itemStarted, () => _password ??= password?.Invoke(), cancellationToken);
        var result = _archive.Extract(indices, (uint)indices.Length, 0, callback);

        cancellationToken.ThrowIfCancellationRequested();
        if (result != SevenZipIds.S_OK && callback.Failures.Count == 0)
        {
            throw new IOException($"7-Zip could not extract from {System.IO.Path.GetFileName(FilePath)} (0x{result:X8}).");
        }

        return callback.Failures;
    }

    private List<ArchiveItem> ReadItems()
    {
        var items = new List<ArchiveItem>();
        if (_archive.GetNumberOfItems(out var count) != SevenZipIds.S_OK)
        {
            return items;
        }

        var fallbackName = System.IO.Path.GetFileNameWithoutExtension(FilePath);
        using var value = new PropVariant();
        for (uint i = 0; i < count; i++)
        {
            var path = Get(i, SevenZipIds.Path, value, v => v.AsString()) ?? string.Empty;
            path = path.Replace('/', '\\').Trim('\\');
            if (path.Length == 0)
            {
                // Single-stream formats (gz, bz2, xz) often store no name: the archive's own name without its extension.
                path = count == 1 ? fallbackName : $"{fallbackName}.{i}";
            }

            var isFolder = Get(i, SevenZipIds.IsDir, value, v => v.AsBool());
            var size = (long)(Get(i, SevenZipIds.Size, value, v => v.AsUInt64()) ?? 0);
            var modified = Get(i, SevenZipIds.MTime, value, v => v.AsFileTimeUtc());
            var encrypted = Get(i, SevenZipIds.Encrypted, value, v => v.AsBool());
            items.Add(new ArchiveItem(i, path, isFolder, size, modified, encrypted));
        }

        return items;
    }

    private T? Get<T>(uint index, uint propId, PropVariant value, Func<PropVariant, T> read)
    {
        try
        {
            return _archive.GetProperty(index, propId, value.Pointer) == SevenZipIds.S_OK ? read(value) : default;
        }
        finally
        {
            value.Clear();
        }
    }

    [ComVisible(true)]
    private sealed class ExtractCallback(
        SevenZipArchive owner,
        IReadOnlyDictionary<uint, string> targets,
        Action<long> bytesWritten,
        Action<ArchiveItem> itemStarted,
        Func<string?> password,
        CancellationToken cancellationToken) : IArchiveExtractCallback, ICryptoGetTextPassword, IDisposable
    {
        private OutStream? _current;
        private string? _currentPath;
        private uint _currentIndex;

        public Dictionary<uint, string> Failures { get; } = [];

        public int SetTotal(ulong total) => SevenZipIds.S_OK;

        public int SetCompleted(IntPtr completeValue) => cancellationToken.IsCancellationRequested ? SevenZipIds.E_ABORT : SevenZipIds.S_OK;

        public int GetStream(uint index, out ISequentialOutStream? outStream, int askExtractMode)
        {
            outStream = null;
            if (cancellationToken.IsCancellationRequested)
            {
                return SevenZipIds.E_ABORT;
            }

            if (askExtractMode != SevenZipIds.AskExtract || !targets.TryGetValue(index, out var target))
            {
                return SevenZipIds.S_OK;
            }

            var item = owner.Items[(int)index];
            itemStarted(item);
            try
            {
                if (item.IsFolder)
                {
                    Directory.CreateDirectory(target);
                    return SevenZipIds.S_OK;
                }

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
                _current = new OutStream(stream, bytesWritten);
                _currentPath = target;
                _currentIndex = index;
                outStream = _current;
                return SevenZipIds.S_OK;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Failures[index] = ex.Message;
                return SevenZipIds.S_OK;
            }
        }

        public int PrepareOperation(int askExtractMode) => SevenZipIds.S_OK;

        public int SetOperationResult(int operationResult)
        {
            if (_current is null)
            {
                return SevenZipIds.S_OK;
            }

            var ok = operationResult == SevenZipIds.ResultOk;
            if (!ok)
            {
                Failures[_currentIndex] = operationResult switch
                {
                    SevenZipIds.ResultWrongPassword => "Wrong password.",
                    SevenZipIds.ResultCrcError => "CRC error: the archive is damaged or the password is wrong.",
                    SevenZipIds.ResultDataError => "Data error: the archive is damaged or the password is wrong.",
                    SevenZipIds.ResultUnsupportedMethod => "Unsupported compression method.",
                    _ => $"Extraction failed (result {operationResult}).",
                };
            }

            CloseCurrent(keep: ok);
            return SevenZipIds.S_OK;
        }

        public int CryptoGetTextPassword(out string? result)
        {
            result = password();
            return result is null ? SevenZipIds.E_ABORT : SevenZipIds.S_OK;
        }

        public void Dispose() => CloseCurrent(keep: false);

        /// <summary>Closes the file being written; a file not kept (failed, cancelled) is removed.</summary>
        public void CloseCurrent(bool keep)
        {
            if (_current is null)
            {
                return;
            }

            _current.Dispose();
            _current = null;
            if (!keep && _currentPath is not null)
            {
                try
                {
                    File.Delete(_currentPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left behind; nothing else to do.
                }
            }
            else if (keep && owner.Items[(int)_currentIndex].ModifiedUtc is { } modified && _currentPath is not null)
            {
                try
                {
                    File.SetLastWriteTimeUtc(_currentPath, modified);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The time is a nicety.
                }
            }

            _currentPath = null;
        }
    }
}
