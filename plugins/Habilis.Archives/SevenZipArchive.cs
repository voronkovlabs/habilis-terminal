using System.Runtime.InteropServices;

namespace Habilis.Archives;

/// <summary>One item of an archive as 7-Zip lists it; <see cref="Path"/> uses '\' and has no leading separator.</summary>
public sealed record ArchiveItem(uint Index, string Path, bool IsFolder, long Size, DateTime? ModifiedUtc, bool Encrypted);

/// <summary>A file or folder to add to an archive: its path inside and the local file (null for a folder).</summary>
public sealed record NewArchiveItem(string Path, string? SourceFile, long Size, DateTime ModifiedUtc, FileAttributes Attributes)
{
    public bool IsFolder => SourceFile is null;
}

/// <summary>An open archive: its items, extraction and updated copies. Not thread-safe; one operation at a time.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SevenZipArchive : IArchive
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
    /// partial file. Returns the indices that failed with the reason. A wrong password is asked for again until the
    /// items come out or the question is cancelled (issue #157); <see cref="PasswordWasWrong"/> tells the question.
    /// </summary>
    /// <param name="password">Asked for encrypted items when no password is known yet; null skips them.</param>
    /// <exception cref="OperationCanceledException">Cancelled; the item in progress is removed.</exception>
    public IReadOnlyDictionary<uint, string> Extract(
        IReadOnlyDictionary<uint, string> targets, Action<long> bytesWritten, Action<ArchiveItem> itemStarted, Func<string?>? password, CancellationToken cancellationToken)
    {
        var failures = new Dictionary<uint, string>();
        var remaining = targets;
        while (true)
        {
            var indices = remaining.Keys.Order().ToArray();
            using var callback = new ExtractCallback(this, remaining, bytesWritten, itemStarted, () => _password ??= password?.Invoke(), cancellationToken);
            var result = _archive.Extract(indices, (uint)indices.Length, 0, callback);

            cancellationToken.ThrowIfCancellationRequested();
            if (callback.PasswordRefused)
            {
                // The question was cancelled: what has not come out does not.
                var reason = PasswordWasWrong ? "Wrong password." : "No password was given.";
                foreach (var index in indices.Where(i => !Items[(int)i].IsFolder && !callback.Done.Contains(i)))
                {
                    failures[index] = reason;
                }

                return failures;
            }

            if (result != SevenZipIds.S_OK && callback.Failures.Count == 0)
            {
                throw new IOException($"7-Zip could not extract from {System.IO.Path.GetFileName(FilePath)} (0x{result:X8}).");
            }

            foreach (var (index, reason) in callback.Failures.Where(f => !callback.PasswordFailures.Contains(f.Key)))
            {
                failures[index] = reason;
            }

            if (callback.PasswordFailures.Count == 0)
            {
                PasswordWasWrong = false;
                return failures;
            }

            if (password is null)
            {
                foreach (var index in callback.PasswordFailures)
                {
                    failures[index] = callback.Failures[index];
                }

                return failures;
            }

            _password = null;
            PasswordWasWrong = true;
            remaining = callback.PasswordFailures.ToDictionary(i => i, i => remaining[i]);
        }
    }

    /// <summary>The last password given did not fit: the next question says so.</summary>
    public bool PasswordWasWrong { get; private set; }

    /// <summary>The password given while opening or extracting, for reopening an updated copy.</summary>
    public string? Password => _password;

    /// <summary>
    /// Writes an updated copy of the archive to <paramref name="outputPath"/> (a new file): the items at
    /// <paramref name="keep"/> unchanged, then <paramref name="add"/>. The caller replaces the original with it.
    /// </summary>
    /// <exception cref="IOException">The format cannot write, a file to add cannot be read, or 7-Zip failed.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the caller deletes the partial copy.</exception>
    public void Update(string outputPath, IReadOnlyList<uint> keep, IReadOnlyList<NewArchiveItem> add, Action<long> bytesRead, Action<string> itemStarted, CancellationToken cancellationToken)
    {
        if (!Format.CanWrite || _archive is not IOutArchive writer)
        {
            throw new IOException($"{Format.Name} archives cannot be changed.");
        }

        using var output = new OutStream(new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024), _ => { });
        using var callback = new UpdateCallback(keep, add, bytesRead, itemStarted, cancellationToken);
        var result = writer.UpdateItems(output, (uint)(keep.Count + add.Count), callback);
        cancellationToken.ThrowIfCancellationRequested();
        if (result != SevenZipIds.S_OK)
        {
            throw new IOException(callback.Error ?? $"7-Zip could not write {System.IO.Path.GetFileName(FilePath)} (0x{result:X8}).");
        }
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

        /// <summary>Items that failed in a way a wrong password explains.</summary>
        public HashSet<uint> PasswordFailures { get; } = [];

        /// <summary>Items that came out.</summary>
        public HashSet<uint> Done { get; } = [];

        /// <summary>The question for the password was cancelled.</summary>
        public bool PasswordRefused { get; private set; }

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
            if (ok)
            {
                Done.Add(_currentIndex);
            }
            else
            {
                // The older zip cipher checks a password by one byte only, so a wrong one can show as damage instead.
                if (operationResult == SevenZipIds.ResultWrongPassword ||
                    (owner.Items[(int)_currentIndex].Encrypted && operationResult is SevenZipIds.ResultCrcError or SevenZipIds.ResultDataError))
                {
                    PasswordFailures.Add(_currentIndex);
                }

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
            PasswordRefused = result is null;
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

    [ComVisible(true)]
    private sealed class UpdateCallback(
        IReadOnlyList<uint> keep,
        IReadOnlyList<NewArchiveItem> add,
        Action<long> bytesRead,
        Action<string> itemStarted,
        CancellationToken cancellationToken) : IArchiveUpdateCallback, IDisposable
    {
        private readonly List<SourceStream> _opened = [];

        /// <summary>Why a file to add could not be read.</summary>
        public string? Error { get; private set; }

        public int SetTotal(ulong total) => SevenZipIds.S_OK;

        public int SetCompleted(IntPtr completeValue) => cancellationToken.IsCancellationRequested ? SevenZipIds.E_ABORT : SevenZipIds.S_OK;

        public int GetUpdateItemInfo(uint index, IntPtr newData, IntPtr newProperties, IntPtr indexInArchive)
        {
            var kept = index < keep.Count;
            if (newData != IntPtr.Zero)
            {
                Marshal.WriteInt32(newData, kept ? 0 : 1);
            }

            if (newProperties != IntPtr.Zero)
            {
                Marshal.WriteInt32(newProperties, kept ? 0 : 1);
            }

            if (indexInArchive != IntPtr.Zero)
            {
                Marshal.WriteInt32(indexInArchive, kept ? (int)keep[(int)index] : -1);
            }

            return SevenZipIds.S_OK;
        }

        public int GetProperty(uint index, uint propId, IntPtr value)
        {
            if (propId == SevenZipIds.IsAnti)
            {
                PropVariant.WriteBool(value, false);
                return SevenZipIds.S_OK;
            }

            if (index < keep.Count)
            {
                return SevenZipIds.S_OK;
            }

            var item = add[(int)index - keep.Count];
            switch (propId)
            {
                case SevenZipIds.Path:
                    PropVariant.WriteString(value, item.Path);
                    break;
                case SevenZipIds.IsDir:
                    PropVariant.WriteBool(value, item.IsFolder);
                    break;
                case SevenZipIds.Size:
                    PropVariant.WriteUInt64(value, item.IsFolder ? 0 : (ulong)item.Size);
                    break;
                case SevenZipIds.Attrib:
                    PropVariant.WriteUInt32(value, (uint)item.Attributes);
                    break;
                case SevenZipIds.MTime or SevenZipIds.CTime or SevenZipIds.ATime:
                    PropVariant.WriteFileTime(value, item.ModifiedUtc);
                    break;
            }

            return SevenZipIds.S_OK;
        }

        public int GetStream(uint index, out ISequentialInStream? inStream)
        {
            inStream = null;
            if (cancellationToken.IsCancellationRequested)
            {
                return SevenZipIds.E_ABORT;
            }

            if (index < keep.Count || add[(int)index - keep.Count] is not { SourceFile: { } file } item)
            {
                return SevenZipIds.S_OK;
            }

            itemStarted(item.Path);
            try
            {
                // Opened here so that a missing or locked file fails now, with its name.
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                }

                var stream = new SourceStream(file, bytesRead);
                lock (_opened)
                {
                    _opened.Add(stream);
                }

                inStream = stream;
                return SevenZipIds.S_OK;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error = $"{file}: {ex.Message}";
                return SevenZipIds.E_FAIL;
            }
        }

        public int SetOperationResult(int operationResult) => SevenZipIds.S_OK;

        /// <summary>Closes the files 7-Zip did not read to the end (a failed or cancelled update).</summary>
        public void Dispose()
        {
            lock (_opened)
            {
                foreach (var stream in _opened)
                {
                    stream.Dispose();
                }

                _opened.Clear();
            }
        }
    }
}
