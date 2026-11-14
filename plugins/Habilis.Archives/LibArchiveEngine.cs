using System.Runtime.InteropServices;

namespace Habilis.Archives;

/// <summary>
/// Archives on a Mac (issue #281) through the system's own libarchive, the library behind its tar: nothing to ship or
/// download. It reads zip, 7z, rar, tar and its compressions, cab, lha, cpio, iso, xar and more, by signature; a lone
/// compressed file (log.gz) shows the one file inside. It writes zip, 7z and tar with gzip, bzip2 or xz (issue #285):
/// a change makes a new copy with the kept entries and the added ones, which the provider swaps in.
/// </summary>
public sealed class LibArchiveEngine : IArchiveEngine
{
    public static string LibraryPath => OperatingSystem.IsMacOS() ? "/usr/lib/libarchive.2.dylib" : "libarchive.so.13";

    private const int Ok = 0;
    private const int Eof = 1;
    private const int Warn = -20;
    private const uint TypeMask = 0xF000; // S_IFMT
    private const uint TypeFolder = 0x4000; // S_IFDIR
    private const uint TypeFile = 0x8000; // S_IFREG

    /// <summary>What libarchive reads that is an archive to the user; Ctrl+PgDn tries any file.</summary>
    private static readonly string[] KnownExtensions =
    [
        "zip", "zipx", "7z", "rar", "tar", "gz", "gzip", "tgz", "tpz", "bz2", "bzip2", "tbz", "tbz2", "xz", "txz", "lzma", "tlz",
        "z", "taz", "zst", "tzst", "cab", "lzh", "lha", "cpio", "rpm", "deb", "iso", "xar", "jar", "apk",
    ];

    private IntPtr _handle;
    private readonly Native _native;

    private LibArchiveEngine(IntPtr handle)
    {
        _handle = handle;
        _native = new Native(handle);
        Formats = [new ArchiveFormat("libarchive", Guid.Empty, KnownExtensions)];
        Extensions = new HashSet<string>(KnownExtensions, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ArchiveFormat> Formats { get; }

    public IReadOnlySet<string> Extensions { get; }

    /// <summary>The system's libarchive, or null when it is not there or lacks a function this needs.</summary>
    public static LibArchiveEngine? TryLoad()
    {
        if (!NativeLibrary.TryLoad(LibraryPath, out var handle))
        {
            return null;
        }

        try
        {
            return new LibArchiveEngine(handle);
        }
        catch (EntryPointNotFoundException)
        {
            NativeLibrary.Free(handle);
            return null;
        }
    }

    public void Free()
    {
        // The system library stays loaded for the process; only this engine's use of it ends.
        _handle = IntPtr.Zero;
    }

    public IArchive? Open(string path, bool anyFormat, Func<bool, string?>? password = null)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        if (!anyFormat && !Extensions.Contains(Path.GetExtension(path).TrimStart('.')))
        {
            return null;
        }

        using (File.OpenRead(path))
        {
            // The file can be read: what follows fails only on its content.
        }

        string? known = null;
        var wrong = false;
        while (true)
        {
            var listing = List(path, known, raw: false);
            if (listing.Items is not null)
            {
                return new LibArchive(_native, path, listing, raw: false, known);
            }

            if (listing.NeedsPassword)
            {
                known = password?.Invoke(wrong) ?? throw new OperationCanceledException();
                wrong = true;
                continue;
            }

            // An archive libarchive knows but cannot read, such as a 7z with encrypted names: its words.
            if (listing.Error is { Length: > 0 } error)
            {
                throw new IOException($"{Path.GetFileName(path)}: {error}");
            }

            // Not an archive: perhaps one compressed file (gz, bz2, xz, zst...).
            var single = List(path, null, raw: true);
            return single.Items is { Count: 1 } && single.CompressedOnly ? new LibArchive(_native, path, single, raw: true, null) : null;
        }
    }

    /// <param name="Headers">For each item, the number of its header in the archive: entries without a name ("./") are no items.</param>
    /// <param name="WriteFormat">libarchive's writer for this format ("zip", "7zip", "paxr"); null when it is not written.</param>
    /// <param name="WriteFilter">The compression around it ("gzip"...), or null.</param>
    private sealed record Listing(
        IReadOnlyList<ArchiveItem>? Items, IReadOnlyList<int> Headers, string FormatName, string? WriteFormat, string? WriteFilter,
        bool NeedsPassword, bool CompressedOnly, string? Error = null);

    private Listing List(string path, string? password, bool raw)
    {
        using var reader = Reader.Open(_native, path, password, raw);
        if (reader is null)
        {
            return new Listing(null, [], string.Empty, null, null, false, false);
        }

        var items = new List<ArchiveItem>();
        var headers = new List<int>();
        for (var header = 0; ; header++)
        {
            var result = reader.Next(out var entry);
            if (result == Eof)
            {
                break;
            }

            if (result < Warn)
            {
                // Nothing read at all and a passphrase is wanted: encrypted names (7z, rar with -hp).
                var error = reader.Error;
                var needsPassword = items.Count == 0 && error.Contains("passphrase", StringComparison.OrdinalIgnoreCase);
                return items.Count == 0
                    ? new Listing(null, [], string.Empty, null, null, needsPassword, false, error)
                    : new Listing(items, headers, reader.FormatName, null, null, false, false); // damaged after these: read, never written
            }

            var name = raw ? RawName(path) : CleanPath(_native.PathName(entry));
            if (name.Length == 0)
            {
                continue;
            }

            var isFolder = !raw && (_native.FileType(entry) & TypeMask) == TypeFolder;
            var size = _native.SizeIsSet(entry) != 0 ? _native.Size(entry) : 0;
            var modified = _native.MtimeIsSet(entry) != 0 ? DateTimeOffset.FromUnixTimeSeconds(_native.Mtime(entry)).UtcDateTime : (DateTime?)null;
            items.Add(new ArchiveItem((uint)items.Count, name, isFolder, isFolder ? 0 : size, modified, _native.IsEncrypted(entry) > 0));
            headers.Add(header);
        }

        if (raw)
        {
            // A lone compressed file is named by its compression ("gzip").
            var filter = reader.FilterName;
            return new Listing(items, headers, filter.Length > 0 ? filter : "Archive", null, null, false, reader.FilterCount > 1);
        }

        var (writeFormat, writeFilter) = Writer(reader.FormatCode, reader.FilterCode);
        var format = reader.FormatName;
        return new Listing(items, headers, format.Length > 0 ? format : "Archive", writeFormat, writeFilter, false, false);
    }

    /// <summary>How libarchive writes the format it read: zip, 7z, and tar plain or with gzip, bzip2 or xz.</summary>
    internal static (string? Format, string? Filter) Writer(int formatCode, int filterCode)
    {
        const int zip = 0x50000, sevenZip = 0xE0000, tar = 0x30000;
        string? filter = filterCode switch { 0 => null, 1 => "gzip", 2 => "bzip2", 6 => "xz", _ => "?" };
        return (formatCode & 0xFF0000) switch
        {
            zip when filter is null => ("zip", null),
            sevenZip when filter is null => ("7zip", null),
            tar when filter != "?" => ("paxr", filter),
            _ => (null, null),
        };
    }

    /// <summary>The one file inside log.gz: log.</summary>
    private static string RawName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".tgz" or ".tpz" or ".taz" or ".tbz" or ".tbz2" or ".txz" or ".tlz" or ".tzst" ? name + ".tar" : name;
    }

    /// <summary>
    /// An entry's name as the panels take it: '\' between folders, no leading separator, no "." and no ".." (a name
    /// that climbs out of the archive is not to be trusted).
    /// </summary>
    internal static string CleanPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p is not "." and not "..");
        return string.Join('\\', parts);
    }

    /// <summary>One open archive: its listing, and a fresh reader for each extraction or update (libarchive reads forward only).</summary>
    private sealed class LibArchive : IArchive
    {
        private readonly Native _native;
        private readonly bool _raw;
        private readonly IReadOnlyList<ArchiveItem> _items;
        private readonly Dictionary<int, int> _itemOfHeader;
        private readonly IReadOnlyList<int> _headers;
        private readonly string? _writeFormat;
        private readonly string? _writeFilter;
        private string? _password;

        public LibArchive(Native native, string path, Listing listing, bool raw, string? password)
        {
            _native = native;
            _raw = raw;
            _items = listing.Items!;
            _headers = listing.Headers;
            _itemOfHeader = _headers.Select((header, item) => (header, item)).ToDictionary(p => p.header, p => p.item);
            _writeFormat = raw ? null : listing.WriteFormat;
            _writeFilter = listing.WriteFilter;
            _password = password;
            FilePath = path;

            // Encrypted entries would come out of a rewrite unencrypted: such an archive is only read.
            Format = new ArchiveFormat(listing.FormatName, Guid.Empty, [], CanWrite: _writeFormat is not null && !_items.Any(i => i.Encrypted));
        }

        public string FilePath { get; }

        public ArchiveFormat Format { get; }

        public IReadOnlyList<ArchiveItem> Items => _items;

        public bool PasswordWasWrong { get; private set; }

        public string? Password => _password;

        public void Dispose()
        {
        }

        public IReadOnlyDictionary<uint, string> Extract(
            IReadOnlyDictionary<uint, string> targets, Action<long> bytesWritten, Action<ArchiveItem> itemStarted, Func<string?>? password, CancellationToken cancellationToken)
        {
            var failures = new Dictionary<uint, string>();
            var remaining = targets.ToDictionary();
            while (true)
            {
                if (_password is null && password is not null && remaining.Keys.Any(IsEncryptedFile))
                {
                    _password = password();
                    if (_password is null)
                    {
                        var reason = PasswordWasWrong ? "Wrong password." : "No password was given.";
                        foreach (var index in remaining.Keys.Where(IsEncryptedFile).ToList())
                        {
                            failures[index] = reason;
                            remaining.Remove(index);
                        }
                    }
                }

                var passwordFailures = Pass(remaining, failures, bytesWritten, itemStarted, cancellationToken);
                if (passwordFailures.Count == 0)
                {
                    PasswordWasWrong = false;
                    return failures;
                }

                if (password is null)
                {
                    foreach (var (index, reason) in passwordFailures)
                    {
                        failures[index] = reason;
                    }

                    return failures;
                }

                _password = null;
                PasswordWasWrong = true;
                remaining = passwordFailures.Keys.ToDictionary(i => i, i => targets[i]);
            }
        }

        private bool IsEncryptedFile(uint index) => _items[(int)index] is { Encrypted: true, IsFolder: false };

        /// <summary>
        /// The kept entries are copied as they are, header and data, in their order; then the new ones follow, each from
        /// its local file. libarchive writes the same format and compression the archive has.
        /// </summary>
        public void Update(string outputPath, IReadOnlyList<uint> keep, IReadOnlyList<NewArchiveItem> add, Action<long> bytesRead, Action<string> itemStarted, CancellationToken cancellationToken)
        {
            if (!Format.CanWrite || _writeFormat is null)
            {
                throw new IOException($"{Format.Name} archives cannot be changed here.");
            }

            var kept = keep.Select(i => _headers[(int)i]).ToHashSet();
            using var reader = Reader.Open(_native, FilePath, _password, raw: false) ?? throw new IOException($"{Path.GetFileName(FilePath)} cannot be read.");
            using var writer = ArchiveWriter.Open(_native, outputPath, _writeFormat, _writeFilter);
            var buffer = new byte[256 * 1024];
            for (var header = 0; ; header++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = reader.Next(out var entry);
                if (result == Eof)
                {
                    break;
                }

                if (result < Warn)
                {
                    throw new IOException(reader.Error);
                }

                if (!kept.Contains(header))
                {
                    continue;
                }

                writer.Header(entry);
                long read;
                while ((read = reader.Read(buffer)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.Data(buffer, (int)read);
                }

                if (read < 0)
                {
                    throw new IOException(reader.Error);
                }

                writer.FinishEntry();
            }

            foreach (var item in add)
            {
                cancellationToken.ThrowIfCancellationRequested();
                itemStarted(item.Path);
                writer.Add(item, buffer, bytesRead, cancellationToken);
            }

            writer.Close();
        }

        /// <summary>One reading of the archive; returns the encrypted items that failed, perhaps for a wrong password.</summary>
        private Dictionary<uint, string> Pass(Dictionary<uint, string> targets, Dictionary<uint, string> failures, Action<long> bytesWritten, Action<ArchiveItem> itemStarted, CancellationToken cancellationToken)
        {
            var passwordFailures = new Dictionary<uint, string>();
            if (targets.Count == 0)
            {
                return passwordFailures;
            }

            using var reader = Reader.Open(_native, FilePath, _password, _raw) ?? throw new IOException($"{Path.GetFileName(FilePath)} cannot be read.");
            var left = new HashSet<uint>(targets.Keys);
            var buffer = new byte[256 * 1024];
            for (var header = 0; left.Count > 0; header++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = reader.Next(out _);
                if (result == Eof || result < Warn)
                {
                    var reason = result == Eof ? "The item is no longer in the archive." : reader.Error;
                    foreach (var missing in left)
                    {
                        failures[missing] = reason;
                    }

                    break;
                }

                if (!_itemOfHeader.TryGetValue(header, out var position) || !left.Remove((uint)position))
                {
                    continue;
                }

                var index = (uint)position;
                var target = targets[index];
                var item = _items[position];
                itemStarted(item);
                if (item.IsFolder)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                if (WriteOut(reader, target, buffer, bytesWritten, cancellationToken) is { } error)
                {
                    if (item.Encrypted)
                    {
                        passwordFailures[index] = error;
                    }
                    else
                    {
                        failures[index] = error;
                    }
                }
                else if (item.ModifiedUtc is { } modified)
                {
                    File.SetLastWriteTimeUtc(target, modified);
                }
            }

            return passwordFailures;
        }

        /// <summary>The current entry's data to <paramref name="target"/>; null when done, else why it failed (no file is left).</summary>
        private static string? WriteOut(Reader reader, string target, byte[] buffer, Action<long> bytesWritten, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var done = false;
            try
            {
                using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = reader.Read(buffer);
                        if (read == 0)
                        {
                            break;
                        }

                        if (read < 0)
                        {
                            return reader.Error;
                        }

                        file.Write(buffer, 0, (int)read);
                        bytesWritten(read);
                    }
                }

                done = true;
                return null;
            }
            finally
            {
                if (!done)
                {
                    File.Delete(target);
                }
            }
        }
    }

    /// <summary>One libarchive reader over the file: forward only, freed on dispose.</summary>
    private sealed class Reader : IDisposable
    {
        private readonly Native _native;
        private IntPtr _archive;

        private Reader(Native native, IntPtr archive)
        {
            _native = native;
            _archive = archive;
        }

        public static Reader? Open(Native native, string path, string? password, bool raw)
        {
            var archive = native.ReadNew();
            if (archive == IntPtr.Zero)
            {
                return null;
            }

            var reader = new Reader(native, archive);
            native.SupportFilterAll(archive);
            if (raw)
            {
                native.SupportFormatRaw(archive);
            }
            else
            {
                native.SupportFormatAll(archive);
            }

            if (password is not null)
            {
                native.AddPassphrase(archive, password);
            }

            if (native.OpenFileName(archive, path, 64 * 1024) != Ok)
            {
                reader.Dispose();
                return null;
            }

            return reader;
        }

        public string Error => Marshal.PtrToStringUTF8(_native.ErrorString(_archive)) ?? "The archive cannot be read.";

        public string FormatName => Marshal.PtrToStringUTF8(_native.FormatName(_archive)) ?? string.Empty;

        public int FormatCode => _native.FormatCode(_archive);

        /// <summary>The outermost compression's code: 0 none, 1 gzip, 2 bzip2, 6 xz...</summary>
        public int FilterCode => _native.FilterCode(_archive, 0);

        /// <summary>The outermost compression: "gzip", "bzip2", "xz"...</summary>
        public string FilterName => Marshal.PtrToStringUTF8(_native.FilterName(_archive, 0)) ?? string.Empty;

        /// <summary>The compressions in front of the format, plus "none": more than one for log.gz.</summary>
        public int FilterCount => _native.FilterCount(_archive);

        public int Next(out IntPtr entry) => _native.NextHeader(_archive, out entry);

        public long Read(byte[] buffer)
        {
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                return (long)_native.ReadData(_archive, handle.AddrOfPinnedObject(), (nuint)buffer.Length);
            }
            finally
            {
                handle.Free();
            }
        }

        public void Dispose()
        {
            var archive = Interlocked.Exchange(ref _archive, IntPtr.Zero);
            if (archive != IntPtr.Zero)
            {
                _native.ReadFree(archive);
            }
        }
    }

    /// <summary>One libarchive writer to a new file; freed on dispose, and the file is complete only after <see cref="Close"/>.</summary>
    private sealed class ArchiveWriter : IDisposable
    {
        private readonly Native _native;
        private IntPtr _archive;

        private ArchiveWriter(Native native, IntPtr archive)
        {
            _native = native;
            _archive = archive;
        }

        public static ArchiveWriter Open(Native native, string path, string format, string? filter)
        {
            var archive = native.WriteNew();
            if (archive == IntPtr.Zero)
            {
                throw new IOException("libarchive could not start writing.");
            }

            var writer = new ArchiveWriter(native, archive);
            try
            {
                writer.Check(native.WriteSetFormatByName(archive, format));
                if (filter is not null)
                {
                    writer.Check(native.WriteAddFilterByName(archive, filter));
                }

                writer.Check(native.WriteOpenFileName(archive, path));
                return writer;
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }

        public void Header(IntPtr entry) => Check(_native.WriteHeader(_archive, entry));

        public void Data(byte[] buffer, int count)
        {
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var offset = 0;
                while (offset < count)
                {
                    var written = (long)_native.WriteData(_archive, handle.AddrOfPinnedObject() + offset, (nuint)(count - offset));
                    if (written <= 0)
                    {
                        throw new IOException(Error);
                    }

                    offset += (int)written;
                }
            }
            finally
            {
                handle.Free();
            }
        }

        public void FinishEntry() => Check(_native.WriteFinishEntry(_archive));

        /// <summary>A new folder or file; '\' in its path becomes '/', and it keeps its local permissions.</summary>
        public void Add(NewArchiveItem item, byte[] buffer, Action<long> bytesRead, CancellationToken cancellationToken)
        {
            var entry = _native.EntryNew();
            try
            {
                var source = item.SourceFile;
                var size = source is null ? 0 : new FileInfo(source).Length;
                _native.EntrySetPathName(entry, item.Path.Replace('\\', '/') + (item.IsFolder ? "/" : string.Empty));
                _native.EntrySetFileType(entry, item.IsFolder ? TypeFolder : TypeFile);
                _native.EntrySetPerm(entry, Permissions(source, item.IsFolder));
                _native.EntrySetSize(entry, size);
                _native.EntrySetMtime(entry, new DateTimeOffset(DateTime.SpecifyKind(item.ModifiedUtc, DateTimeKind.Utc)).ToUnixTimeSeconds(), 0);
                Header(entry);
                if (source is not null)
                {
                    using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                    var left = size;
                    int read;
                    while (left > 0 && (read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left))) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Data(buffer, read);
                        bytesRead(read);
                        left -= read;
                    }
                }

                FinishEntry();
            }
            finally
            {
                _native.EntryFree(entry);
            }
        }

        public void Close() => Check(_native.WriteClose(_archive));

        public void Dispose()
        {
            var archive = Interlocked.Exchange(ref _archive, IntPtr.Zero);
            if (archive != IntPtr.Zero)
            {
                _native.WriteFree(archive);
            }
        }

        private string Error => Marshal.PtrToStringUTF8(_native.ErrorString(_archive)) ?? "libarchive could not write the archive.";

        private void Check(int result)
        {
            if (result < Warn)
            {
                throw new IOException(Error);
            }
        }

        private static uint Permissions(string? source, bool folder)
        {
            const uint fallbackFile = 0x1A4, fallbackFolder = 0x1ED; // 0644, 0755
            if (source is null || OperatingSystem.IsWindows())
            {
                return folder ? fallbackFolder : fallbackFile;
            }

            try
            {
                return (uint)File.GetUnixFileMode(source) & 0x1FF;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return fallbackFile;
            }
        }
    }

    /// <summary>The functions of libarchive this uses, looked up once.</summary>
    private sealed class Native
    {
        public Native(IntPtr handle)
        {
            ReadNew = Get<NewFunction>(handle, "archive_read_new");
            SupportFilterAll = Get<ArchiveFunction>(handle, "archive_read_support_filter_all");
            SupportFormatAll = Get<ArchiveFunction>(handle, "archive_read_support_format_all");
            SupportFormatRaw = Get<ArchiveFunction>(handle, "archive_read_support_format_raw");
            AddPassphrase = Get<StringArgumentFunction>(handle, "archive_read_add_passphrase");
            OpenFileName = Get<OpenFileNameFunction>(handle, "archive_read_open_filename");
            NextHeader = Get<NextHeaderFunction>(handle, "archive_read_next_header");
            ReadData = Get<DataFunction>(handle, "archive_read_data");
            ReadFree = Get<ArchiveFunction>(handle, "archive_read_free");
            ErrorString = Get<StringFunction>(handle, "archive_error_string");
            FormatName = Get<StringFunction>(handle, "archive_format_name");
            FormatCode = Get<ArchiveFunction>(handle, "archive_format");
            FilterCount = Get<ArchiveFunction>(handle, "archive_filter_count");
            FilterName = Get<FilterNameFunction>(handle, "archive_filter_name");
            FilterCode = Get<FilterCodeFunction>(handle, "archive_filter_code");
            EntryPathNameUtf8 = TryGet<StringFunction>(handle, "archive_entry_pathname_utf8");
            EntryPathName = Get<StringFunction>(handle, "archive_entry_pathname");
            Size = Get<EntryLongFunction>(handle, "archive_entry_size");
            SizeIsSet = Get<ArchiveFunction>(handle, "archive_entry_size_is_set");
            Mtime = Get<EntryLongFunction>(handle, "archive_entry_mtime");
            MtimeIsSet = Get<ArchiveFunction>(handle, "archive_entry_mtime_is_set");
            FileType = Get<EntryUIntFunction>(handle, "archive_entry_filetype");
            IsEncrypted = Get<ArchiveFunction>(handle, "archive_entry_is_encrypted");

            WriteNew = Get<NewFunction>(handle, "archive_write_new");
            WriteSetFormatByName = Get<StringArgumentFunction>(handle, "archive_write_set_format_by_name");
            WriteAddFilterByName = Get<StringArgumentFunction>(handle, "archive_write_add_filter_by_name");
            WriteOpenFileName = Get<StringArgumentFunction>(handle, "archive_write_open_filename");
            WriteHeader = Get<WriteHeaderFunction>(handle, "archive_write_header");
            WriteData = Get<DataFunction>(handle, "archive_write_data");
            WriteFinishEntry = Get<ArchiveFunction>(handle, "archive_write_finish_entry");
            WriteClose = Get<ArchiveFunction>(handle, "archive_write_close");
            WriteFree = Get<ArchiveFunction>(handle, "archive_write_free");
            EntryNew = Get<NewFunction>(handle, "archive_entry_new");
            EntryFree = Get<VoidFunction>(handle, "archive_entry_free");
            EntrySetPathNameUtf8 = TryGet<EntrySetStringFunction>(handle, "archive_entry_set_pathname_utf8");
            EntrySetPathNameLocal = Get<EntrySetStringFunction>(handle, "archive_entry_set_pathname");
            EntrySetFileType = Get<EntrySetUIntFunction>(handle, "archive_entry_set_filetype");
            EntrySetPerm = Get<EntrySetUIntFunction>(handle, "archive_entry_set_perm");
            EntrySetSize = Get<EntrySetLongFunction>(handle, "archive_entry_set_size");
            EntrySetMtime = Get<EntrySetTimeFunction>(handle, "archive_entry_set_mtime");
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr NewFunction();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ArchiveFunction(IntPtr archive);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void VoidFunction(IntPtr pointer);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int StringArgumentFunction(IntPtr archive, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int OpenFileNameFunction(IntPtr archive, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, nuint blockSize);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int NextHeaderFunction(IntPtr archive, out IntPtr entry);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int WriteHeaderFunction(IntPtr archive, IntPtr entry);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate nint DataFunction(IntPtr archive, IntPtr buffer, nuint size);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr StringFunction(IntPtr archive);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr FilterNameFunction(IntPtr archive, int index);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int FilterCodeFunction(IntPtr archive, int index);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate long EntryLongFunction(IntPtr entry);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint EntryUIntFunction(IntPtr entry);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void EntrySetStringFunction(IntPtr entry, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void EntrySetUIntFunction(IntPtr entry, uint value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void EntrySetLongFunction(IntPtr entry, long value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void EntrySetTimeFunction(IntPtr entry, long seconds, nint nanoseconds);

        public NewFunction ReadNew { get; }

        public ArchiveFunction SupportFilterAll { get; }

        public ArchiveFunction SupportFormatAll { get; }

        public ArchiveFunction SupportFormatRaw { get; }

        public StringArgumentFunction AddPassphrase { get; }

        public OpenFileNameFunction OpenFileName { get; }

        public NextHeaderFunction NextHeader { get; }

        public DataFunction ReadData { get; }

        public ArchiveFunction ReadFree { get; }

        public StringFunction ErrorString { get; }

        public StringFunction FormatName { get; }

        public ArchiveFunction FormatCode { get; }

        public ArchiveFunction FilterCount { get; }

        public FilterNameFunction FilterName { get; }

        public FilterCodeFunction FilterCode { get; }

        public StringFunction? EntryPathNameUtf8 { get; }

        public StringFunction EntryPathName { get; }

        public EntryLongFunction Size { get; }

        public ArchiveFunction SizeIsSet { get; }

        public EntryLongFunction Mtime { get; }

        public ArchiveFunction MtimeIsSet { get; }

        public EntryUIntFunction FileType { get; }

        public ArchiveFunction IsEncrypted { get; }

        public NewFunction WriteNew { get; }

        public StringArgumentFunction WriteSetFormatByName { get; }

        public StringArgumentFunction WriteAddFilterByName { get; }

        public StringArgumentFunction WriteOpenFileName { get; }

        public WriteHeaderFunction WriteHeader { get; }

        public DataFunction WriteData { get; }

        public ArchiveFunction WriteFinishEntry { get; }

        public ArchiveFunction WriteClose { get; }

        public ArchiveFunction WriteFree { get; }

        public NewFunction EntryNew { get; }

        public VoidFunction EntryFree { get; }

        public EntrySetUIntFunction EntrySetFileType { get; }

        public EntrySetUIntFunction EntrySetPerm { get; }

        public EntrySetLongFunction EntrySetSize { get; }

        public EntrySetTimeFunction EntrySetMtime { get; }

        private EntrySetStringFunction? EntrySetPathNameUtf8 { get; }

        private EntrySetStringFunction EntrySetPathNameLocal { get; }

        /// <summary>The entry's name: UTF-8 when libarchive can give it, else as stored (UTF-8 on a Mac).</summary>
        public string? PathName(IntPtr entry) =>
            (EntryPathNameUtf8 is { } utf8 ? Marshal.PtrToStringUTF8(utf8(entry)) : null) ?? Marshal.PtrToStringUTF8(EntryPathName(entry));

        public void EntrySetPathName(IntPtr entry, string path) => (EntrySetPathNameUtf8 ?? EntrySetPathNameLocal)(entry, path);

        private static T Get<T>(IntPtr handle, string name)
            where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));

        private static T? TryGet<T>(IntPtr handle, string name)
            where T : Delegate =>
            NativeLibrary.TryGetExport(handle, name, out var pointer) ? Marshal.GetDelegateForFunctionPointer<T>(pointer) : null;
    }
}
