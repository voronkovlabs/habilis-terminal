using System.Runtime.CompilerServices;
using System.Text;
using HabilisTerminal.Plugins;

namespace Habilis.Samples.FileFormat;

/// <summary>
/// The smallest whole plugin that makes a file look like a folder (docs/plugins.md, recipe "A file format"). It opens
/// <c>.habibundle</c>: a text file where <c>= path/in/bundle.txt</c> starts each inner file.
/// </summary>
public sealed class BundlePlugin : Plugin
{
    /// <summary>Ctrl+PgDn asks about any file, however large: look before reading it all.</summary>
    private const long MaxBundleBytes = 16 * 1024 * 1024;

    public override IEnumerable<string> FileExtensions => ["habibundle"];

    public override async Task<IPanelProvider?> OpenFileAsync(FileOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string text;
        try
        {
            if (new FileInfo(request.Path).Length > MaxBundleBytes)
            {
                return null;
            }

            text = await File.ReadAllTextAsync(request.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginException(ex.Message, ex);
        }

        // Ctrl+PgDn asks about any file, so the content decides: null says "not mine", and the next plugin is asked.
        var bundle = Bundle.Parse(text);
        return bundle is null ? null : new BundlePanel(request.Path, bundle, request.InnerPath);
    }
}

/// <summary>A bundle's inner files, by path with "/" separators.</summary>
internal sealed class Bundle
{
    private const string Marker = "= ";

    private Bundle(IReadOnlyDictionary<string, string> files) => Files = files;

    public IReadOnlyDictionary<string, string> Files { get; }

    /// <summary>
    /// The content of a file is not to be trusted: "../../x" must not be able to write outside the folder F5 copies to.
    /// </summary>
    public static bool IsSafePath(string path) =>
        path.Length > 0 && !path.Contains('\\', StringComparison.Ordinal) && !path.Contains(':', StringComparison.Ordinal) &&
        path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");

    /// <summary>Null when the text is not a bundle: it must start with an inner file's marker, and hold only safe paths.</summary>
    public static Bundle? Parse(string text)
    {
        if (!text.StartsWith(Marker, StringComparison.Ordinal))
        {
            return null;
        }

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var content = new StringBuilder();
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        foreach (var line in text.EndsWith('\n') ? lines[..^1] : lines)
        {
            if (line.StartsWith(Marker, StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    files[current] = content.ToString();
                }

                current = line[Marker.Length..].Trim();
                if (!IsSafePath(current))
                {
                    return null;
                }

                content.Clear();
            }
            else
            {
                content.Append(line).Append('\n');
            }
        }

        files[current!] = content.ToString();
        return new Bundle(files);
    }

    /// <summary>What a folder holds: its subfolders, and its files with their paths.</summary>
    public (IReadOnlyList<string> Folders, IReadOnlyList<string> Files) List(string folder)
    {
        var prefix = folder.Length == 0 ? string.Empty : folder + "/";
        var below = Files.Keys.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(p => p[prefix.Length..]).ToList();
        return (
            below.Where(p => p.Contains('/', StringComparison.Ordinal)).Select(p => p[..p.IndexOf('/', StringComparison.Ordinal)]).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            below.Where(p => !p.Contains('/', StringComparison.Ordinal)).ToList());
    }

    public bool IsFolder(string path) => path.Length == 0 || Files.Keys.Any(p => p.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));
}

/// <summary>A bundle as a folder: lists, goes in and out, views (F3) and copies out (F5). It does not change the file.</summary>
internal sealed class BundlePanel : IPanelProvider, IGetFiles, IViewEntry
{
    private readonly string _path;
    private readonly Bundle _bundle;
    private string _folder = string.Empty;

    public BundlePanel(string path, Bundle bundle, string innerPath)
    {
        _path = path;
        _bundle = bundle;

        // A place from folder history may be gone: then the top.
        var inner = innerPath.Replace('\\', '/');
        if (_bundle.IsFolder(inner))
        {
            _folder = inner;
        }
    }

    public string Title => $"Bundle: {Path.GetFileName(_path)}";

    /// <summary>Reads like a path, so that folder history reopens it: the host splits it at the file.</summary>
    public string Location => _folder.Length == 0 ? _path : _path + "\\" + _folder.Replace('/', '\\');

    public IReadOnlyList<PanelLayout> Layouts { get; } = [new PanelLayout("Bundle", [PanelColumn.Size])];

    public IReadOnlyList<PanelSortKey> SortKeys { get; } = [];

    public IReadOnlyList<InfoLine> Info => [new InfoLine("Bundle", _path), new InfoLine("Files", _bundle.Files.Count.ToString(System.Globalization.CultureInfo.CurrentCulture))];

    public bool CanMove => false;

    public async IAsyncEnumerable<IReadOnlyList<PanelEntry>> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var (folders, files) = _bundle.List(_folder);
        yield return
        [
            .. folders.Select(name => new PanelEntry(name, IsFolder: true)),
            .. files.Select(name => new PanelEntry(name, Size: Encoding.UTF8.GetByteCount(_bundle.Files[Inner(name)]))),
        ];
    }

    public Task<bool> EnterAsync(PanelEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsFolder)
        {
            return Task.FromResult(false);
        }

        _folder = Inner(entry.Name);
        return Task.FromResult(true);
    }

    public Task<bool> GoUpAsync(CancellationToken cancellationToken)
    {
        if (_folder.Length == 0)
        {
            return Task.FromResult(false); // the host leaves the panel and puts the cursor on the file
        }

        var cut = _folder.LastIndexOf('/');
        _folder = cut < 0 ? string.Empty : _folder[..cut];
        return Task.FromResult(true);
    }

    public Task<(string Title, string Text)?> ViewAsync(PanelEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Task.FromResult<(string, string)?>(entry.IsFolder ? null : (entry.Name, _bundle.Files[Inner(entry.Name)]));
    }

    public async Task<OperationOutcome> GetFilesAsync(TransferRequest request, IOperationProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        var copied = 0;
        var skipped = 0;

        // The user can move around the panel while a copy runs: take the folder now.
        var folder = _folder;

        // The host asks again each time: the plugin remembers an answer "for all".
        OverwriteChoice? forAll = null;
        foreach (var entry in request.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = folder.Length == 0 ? entry.Name : folder + "/" + entry.Name;
            var sources = entry.IsFolder
                ? _bundle.Files.Keys.Where(p => p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)).ToList()
                : [prefix];
            foreach (var source in sources)
            {
                progress.Begin(source);
                var relative = folder.Length == 0 ? source : source[(folder.Length + 1)..];
                var target = Path.Combine(request.LocalFolder, relative.Replace('/', '\\'));
                if (File.Exists(target))
                {
                    var choice = forAll ?? await request.AskOverwrite(relative).ConfigureAwait(false);
                    if (choice is OverwriteChoice.OverwriteAll or OverwriteChoice.SkipAll)
                    {
                        forAll = choice;
                    }

                    if (choice == OverwriteChoice.Cancel)
                    {
                        return new OperationOutcome(copied, skipped, 0);
                    }

                    if (choice is OverwriteChoice.Skip or OverwriteChoice.SkipAll)
                    {
                        skipped++;
                        continue;
                    }
                }

                // A cancel or a failure must leave no partial file: write beside it, then move into place.
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var partial = target + ".partial";
                try
                {
                    await File.WriteAllTextAsync(partial, _bundle.Files[source], cancellationToken).ConfigureAwait(false);
                    File.Move(partial, target, overwrite: true);
                }
                finally
                {
                    File.Delete(partial);
                }

                progress.CompleteItem();
                copied++;
            }
        }

        return new OperationOutcome(copied, skipped, 0);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private string Inner(string name) => _folder.Length == 0 ? name : _folder + "/" + name;
}
