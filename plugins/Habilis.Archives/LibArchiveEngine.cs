using System.Runtime.InteropServices;

namespace Habilis.Archives;

/// <summary>
/// Archives on a Mac (issue #281) through the system's own libarchive, the library behind its tar: nothing to ship or
/// download. It reads zip, 7z, rar, tar and its compressions, cab, lha, cpio, iso, xar and more, by signature; a lone
/// compressed file (log.gz) shows the one file inside. Reading only: it does not change archives.
/// </summary>
public sealed class LibArchiveEngine : IArchiveEngine
{
    public static string LibraryPath => OperatingSystem.IsMacOS() ? "/usr/lib/libarchive.2.dylib" : "libarchive.so.13";

    private const int Ok = 0;
    private const int Eof = 1;
    private const int Warn = -20;
    private const uint TypeMask = 0xF000; // S_IFMT
    private const uint TypeFolder = 0x4000; // S_IFDIR

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
            if (listing.Items is { } items)
            {
                return new LibArchive(_native, path, listing.FormatName, items, raw: false, known);
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
            return single.Items is { Count: 1 } one && single.CompressedOnly
                ? new LibArchive(_native, path, single.FormatName, one, raw: true, null)
                : null;
        }
    }

    private sealed record Listing(IReadOnlyList<ArchiveItem>? Items, string FormatName, bool NeedsPassword, bool CompressedOnly, string? Error = null);

    private Listing List(string path, string? password, bool raw)
    {
        using var reader = Reader.Open(_native, path, password, raw);
        if (reader is null)
        {
            return new Listing(null, string.Empty, false, false);
        }

        var items = new List<ArchiveItem>();
        while (true)
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
                return items.Count == 0 ? new Listing(null, string.Empty, needsPassword, false, error) : new Listing(items, reader.FormatName, false, false);
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
        }

        // A lone compressed file is named by its compression ("gzip"), an archive by its format ("ZIP 2.0 (deflation)").
        var format = raw ? reader.FilterName : reader.FormatName;
        return new Listing(items, format.Length > 0 ? format : "Archive", false, raw && reader.FilterCount > 1);
    }

    /// <summary>The one file inside log.gz: log.</summary>
    private static string RawName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".tgz" or ".tpz" or ".taz" ? name + ".tar" : extension is ".tbz" or ".tbz2" ? name + ".tar" : extension is ".txz" or ".tlz" or ".tzst" ? name + ".tar" : name;
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

    /// <summary>One open archive: its listing, and a fresh reader for each extraction (libarchive reads forward only).</summary>
    private sealed class LibArchive(Native native, string path, string formatName, IReadOnlyList<ArchiveItem> items, bool raw, string? password) : IArchive
    {
        private string? _password = password;

        public string FilePath => path;

        public ArchiveFormat Format { get; } = new(formatName, Guid.Empty, [], CanWrite: false);

        public IReadOnlyList<ArchiveItem> Items => items;

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
                if (_password is null && password is not null && remaining.Keys.Any(i => items[(int)i].Encrypted && !items[(int)i].IsFolder))
                {
                    _password = password();
                    if (_password is null)
                    {
                        var reason = PasswordWasWrong ? "Wrong password." : "No password was given.";
                        foreach (var index in remaining.Keys.Where(i => items[(int)i].Encrypted && !items[(int)i].IsFolder).ToList())
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

        public void Update(string outputPath, IReadOnlyList<uint> keep, IReadOnlyList<NewArchiveItem> add, Action<long> bytesRead, Action<string> itemStarted, CancellationToken cancellationToken) =>
            throw new IOException($"{Format.Name} archives cannot be changed here.");

        /// <summary>One reading of the archive; returns the encrypted items that failed, perhaps for a wrong password.</summary>
        private Dictionary<uint, string> Pass(Dictionary<uint, string> targets, Dictionary<uint, string> failures, Action<long> bytesWritten, Action<ArchiveItem> itemStarted, CancellationToken cancellationToken)
        {
            var passwordFailures = new Dictionary<uint, string>();
            if (targets.Count == 0)
            {
                return passwordFailures;
            }

            using var reader = Reader.Open(native, path, _password, raw) ?? throw new IOException($"{System.IO.Path.GetFileName(path)} cannot be read.");
            var left = targets.Count;
            var buffer = new byte[256 * 1024];
            for (uint index = 0; left > 0; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = reader.Next(out _);
                if (result == Eof || result < Warn)
                {
                    var reason = result == Eof ? "The item is no longer in the archive." : reader.Error;
                    foreach (var missing in targets.Keys.Where(k => k >= index && !failures.ContainsKey(k)))
                    {
                        failures[missing] = reason;
                    }

                    break;
                }

                if (index >= items.Count || !targets.TryGetValue(index, out var target))
                {
                    continue;
                }

                left--;
                var item = items[(int)index];
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
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
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

    /// <summary>The functions of libarchive this uses, looked up once.</summary>
    private sealed class Native
    {
        public Native(IntPtr handle)
        {
            ReadNew = Get<ReadNewFunction>(handle, "archive_read_new");
            SupportFilterAll = Get<ArchiveFunction>(handle, "archive_read_support_filter_all");
            SupportFormatAll = Get<ArchiveFunction>(handle, "archive_read_support_format_all");
            SupportFormatRaw = Get<ArchiveFunction>(handle, "archive_read_support_format_raw");
            AddPassphrase = Get<AddPassphraseFunction>(handle, "archive_read_add_passphrase");
            OpenFileName = Get<OpenFileNameFunction>(handle, "archive_read_open_filename");
            NextHeader = Get<NextHeaderFunction>(handle, "archive_read_next_header");
            ReadData = Get<ReadDataFunction>(handle, "archive_read_data");
            ReadFree = Get<ArchiveFunction>(handle, "archive_read_free");
            ErrorString = Get<StringFunction>(handle, "archive_error_string");
            FormatName = Get<StringFunction>(handle, "archive_format_name");
            FilterCount = Get<ArchiveFunction>(handle, "archive_filter_count");
            FilterName = Get<FilterNameFunction>(handle, "archive_filter_name");
            var utf8 = NativeLibrary.TryGetExport(handle, "archive_entry_pathname_utf8", out var pointer) ? pointer : IntPtr.Zero;
            EntryPathNameUtf8 = utf8 == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<StringFunction>(utf8);
            EntryPathName = Get<StringFunction>(handle, "archive_entry_pathname");
            Size = Get<EntryLongFunction>(handle, "archive_entry_size");
            SizeIsSet = Get<ArchiveFunction>(handle, "archive_entry_size_is_set");
            Mtime = Get<EntryLongFunction>(handle, "archive_entry_mtime");
            MtimeIsSet = Get<ArchiveFunction>(handle, "archive_entry_mtime_is_set");
            FileType = Get<EntryUIntFunction>(handle, "archive_entry_filetype");
            IsEncrypted = Get<ArchiveFunction>(handle, "archive_entry_is_encrypted");
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr ReadNewFunction();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ArchiveFunction(IntPtr archive);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int AddPassphraseFunction(IntPtr archive, [MarshalAs(UnmanagedType.LPUTF8Str)] string passphrase);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int OpenFileNameFunction(IntPtr archive, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, nuint blockSize);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int NextHeaderFunction(IntPtr archive, out IntPtr entry);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate nint ReadDataFunction(IntPtr archive, IntPtr buffer, nuint size);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr StringFunction(IntPtr archive);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr FilterNameFunction(IntPtr archive, int index);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate long EntryLongFunction(IntPtr entry);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint EntryUIntFunction(IntPtr entry);

        public ReadNewFunction ReadNew { get; }

        public ArchiveFunction SupportFilterAll { get; }

        public ArchiveFunction SupportFormatAll { get; }

        public ArchiveFunction SupportFormatRaw { get; }

        public AddPassphraseFunction AddPassphrase { get; }

        public OpenFileNameFunction OpenFileName { get; }

        public NextHeaderFunction NextHeader { get; }

        public ReadDataFunction ReadData { get; }

        public ArchiveFunction ReadFree { get; }

        public StringFunction ErrorString { get; }

        public StringFunction FormatName { get; }

        public ArchiveFunction FilterCount { get; }

        public FilterNameFunction FilterName { get; }

        public StringFunction? EntryPathNameUtf8 { get; }

        public StringFunction EntryPathName { get; }

        public EntryLongFunction Size { get; }

        public ArchiveFunction SizeIsSet { get; }

        public EntryLongFunction Mtime { get; }

        public ArchiveFunction MtimeIsSet { get; }

        public EntryUIntFunction FileType { get; }

        public ArchiveFunction IsEncrypted { get; }

        /// <summary>The entry's name: UTF-8 when libarchive can give it, else as stored (UTF-8 on a Mac).</summary>
        public string? PathName(IntPtr entry) =>
            (EntryPathNameUtf8 is { } utf8 ? Marshal.PtrToStringUTF8(utf8(entry)) : null) ?? Marshal.PtrToStringUTF8(EntryPathName(entry));

        private static T Get<T>(IntPtr handle, string name)
            where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));
    }
}
