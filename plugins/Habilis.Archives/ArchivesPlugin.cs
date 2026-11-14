using System.Runtime.CompilerServices;
using HabilisTerminal.Plugins;

namespace Habilis.Archives;

/// <summary>
/// Archives as folders, on 7-Zip (issues #51, #149): Enter on an archive's file or Ctrl+PgDn on any file goes in; ".."
/// at the top comes back out to the file.
/// </summary>
public sealed class ArchivesPlugin : Plugin
{
    private IArchiveEngine? _library;

    public override IEnumerable<string> FileExtensions => ArchiveProvider.ExtensionsOpenedOnEnter(_library);

    public override void Initialize(IPluginHost host)
    {
        base.Initialize(host);
        _library = ArchiveEngines.Load(host.PluginDirectory);
    }

    public override Task<IPanelProvider?> OpenFileAsync(FileOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_library is null)
        {
            throw new PluginException($"{ArchiveEngines.MissingName} is missing: archives cannot be opened.");
        }

        try
        {
            return Task.FromResult<IPanelProvider?>(ArchiveProvider.TryOpen(request.Path, request.InnerPath, request.AnyFormat, AskPassword, _library));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Alt+F7 inside an archive (issue #210): every entry, in the archive's order. An entry's content is unpacked to a
    /// temporary file when the search asks for it, which is quick for zip and slower for a solid 7z. Encrypted entries
    /// are listed by name only: a search never asks for a password.
    /// </summary>
    public override IAsyncEnumerable<SearchEntry>? SearchFile(FileSearchSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _library is { } library ? SearchArchive(library, source.Path, cancellationToken) : null;
    }

    /// <summary>The entries of one archive for the search; public for tests.</summary>
    public static async IAsyncEnumerable<SearchEntry> SearchArchive(IArchiveEngine library, string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        using var archive = OpenForSearch(library, path);
        if (archive is null)
        {
            yield break;
        }

        foreach (var item in archive.Items.OrderBy(i => i.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new SearchEntry(item.Path, item.IsFolder, item.IsFolder ? null : item.Size, item.ModifiedUtc)
            {
                OpenAsync = item.IsFolder || item.Encrypted ? null : token => Task.FromResult<Stream>(Unpack(archive, item, token)),
            };
        }
    }

    private static IArchive? OpenForSearch(IArchiveEngine library, string path)
    {
        try
        {
            return library.Open(path, anyFormat: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginException(ex.Message, ex);
        }
    }

    /// <summary>One entry to a temporary file that goes away when the stream is closed.</summary>
    private static FileStream Unpack(IArchive archive, ArchiveItem item, CancellationToken cancellationToken)
    {
        var target = Path.Combine(Path.GetTempPath(), "habi-search-" + Path.GetRandomFileName());
        try
        {
            var failures = archive.Extract(new Dictionary<uint, string> { [item.Index] = target }, _ => { }, _ => { }, password: null, cancellationToken);
            if (failures.TryGetValue(item.Index, out var reason))
            {
                throw new PluginException(reason);
            }

            return new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Delete, 64 * 1024, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        }
        catch
        {
            File.Delete(target);
            throw;
        }
    }

    public override Task ShutdownAsync()
    {
        _library?.Free();
        _library = null;
        return Task.CompletedTask;
    }

    /// <summary>Asked from 7-Zip's thread, which waits for the answer.</summary>
    private string? AskPassword(string prompt) =>
        Host.Dialogs.InputSecretAsync("Password", prompt).GetAwaiter().GetResult();
}
