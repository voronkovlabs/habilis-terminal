using HabilisTerminal.Plugins;

namespace Habilis.Archives;

/// <summary>
/// Archives as folders, on 7-Zip (issues #51, #149): Enter on an archive's file or Ctrl+PgDn on any file goes in; ".."
/// at the top comes back out to the file.
/// </summary>
public sealed class ArchivesPlugin : Plugin
{
    private SevenZipLibrary? _library;

    public override IEnumerable<string> FileExtensions => ArchiveProvider.ExtensionsOpenedOnEnter(_library);

    public override void Initialize(IPluginHost host)
    {
        base.Initialize(host);
        _library = SevenZipLibrary.TryLoad(SevenZipLibrary.PathIn(host.PluginDirectory));
    }

    public override Task<IPanelProvider?> OpenFileAsync(FileOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_library is null)
        {
            throw new PluginException($"{SevenZipLibrary.FileName} is missing: archives cannot be opened.");
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
