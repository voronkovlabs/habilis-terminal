namespace Habilis.Archives;

/// <summary>
/// What reads archives (issue #281): 7z.dll on Windows, the system's libarchive on a Mac. Lists the formats and opens
/// archives with them.
/// </summary>
public interface IArchiveEngine
{
    IReadOnlyList<ArchiveFormat> Formats { get; }

    /// <summary>Every extension a format claims (zip, 7z, rar, tar, gz, iso...), without the dot.</summary>
    IReadOnlySet<string> Extensions { get; }

    /// <summary>
    /// Opens <paramref name="path"/> with the formats its extension names; with <paramref name="anyFormat"/> every
    /// other format is tried after them (Ctrl+PgDn: by signature). Null when no format accepts the file.
    /// </summary>
    /// <param name="password">Asked when the archive's headers are encrypted; null cancels. True: the last one was wrong.</param>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="OperationCanceledException">The question for the password was cancelled.</exception>
    IArchive? Open(string path, bool anyFormat, Func<bool, string?>? password = null);

    /// <summary>Frees native code when the plugin unloads; nothing may use this engine afterwards.</summary>
    void Free();
}

/// <summary>An open archive: its items, extraction and updated copies. Not thread-safe; one operation at a time.</summary>
public interface IArchive : IDisposable
{
    string FilePath { get; }

    ArchiveFormat Format { get; }

    IReadOnlyList<ArchiveItem> Items { get; }

    /// <summary>
    /// Extracts <paramref name="targets"/> (item index → file path; folders are created). A failed item leaves no
    /// partial file. Returns the indices that failed with the reason. A wrong password is asked for again until the
    /// items come out or the question is cancelled (issue #157); <see cref="PasswordWasWrong"/> tells the question.
    /// </summary>
    /// <param name="password">Asked for encrypted items when no password is known yet; null skips them.</param>
    /// <exception cref="OperationCanceledException">Cancelled; the item in progress is removed.</exception>
    IReadOnlyDictionary<uint, string> Extract(
        IReadOnlyDictionary<uint, string> targets, Action<long> bytesWritten, Action<ArchiveItem> itemStarted, Func<string?>? password, CancellationToken cancellationToken);

    /// <summary>The last password given did not fit: the next question says so.</summary>
    bool PasswordWasWrong { get; }

    /// <summary>The password given while opening or extracting, for reopening an updated copy.</summary>
    string? Password { get; }

    /// <summary>
    /// Writes an updated copy of the archive to <paramref name="outputPath"/> (a new file): the items at
    /// <paramref name="keep"/> unchanged, then <paramref name="add"/>. The caller replaces the original with it.
    /// Only for a <see cref="Format"/> that can write.
    /// </summary>
    /// <exception cref="IOException">The format cannot write, a file to add cannot be read, or the engine failed.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the caller deletes the partial copy.</exception>
    void Update(string outputPath, IReadOnlyList<uint> keep, IReadOnlyList<NewArchiveItem> add, Action<long> bytesRead, Action<string> itemStarted, CancellationToken cancellationToken);
}

/// <summary>The engine of this system, loaded from the plugin's folder or the system.</summary>
public static class ArchiveEngines
{
    private static readonly Lazy<IArchiveEngine?> Shared = new(() =>
        Load(Path.GetDirectoryName(typeof(ArchiveEngines).Assembly.Location) ?? AppContext.BaseDirectory));

    /// <summary>The engine next to this plugin, or null when it cannot be loaded.</summary>
    public static IArchiveEngine? Default => Shared.Value;

    /// <summary>Null when the engine is missing: 7z.dll not in the plugin folder, or no libarchive.</summary>
    public static IArchiveEngine? Load(string pluginDirectory) =>
        OperatingSystem.IsWindows() ? SevenZipLibrary.TryLoad(SevenZipLibrary.PathIn(pluginDirectory))
        : LibArchiveEngine.TryLoad();

    /// <summary>What is missing when <see cref="Load"/> gives null, for the message.</summary>
    public static string MissingName => OperatingSystem.IsWindows() ? SevenZipLibrary.FileName : LibArchiveEngine.LibraryPath;
}
