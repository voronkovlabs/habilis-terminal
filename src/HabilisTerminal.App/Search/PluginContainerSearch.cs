using System.IO;
using System.Runtime.CompilerServices;
using HabilisTerminal.App.Plugins;
using HabilisTerminal.Core.Plugins;
using HabilisTerminal.Core.Search;
using HabilisTerminal.Plugins;

namespace HabilisTerminal.App.Search;

/// <summary>
/// Alt+F7 inside archives (issue #210): the plugins that open a file as a folder, asked through
/// <see cref="Plugin.SearchFile"/>. Taken on the UI thread when the search starts, so the search thread never reads the
/// plugin manager; those plugins are kept busy (no hot reload) until <see cref="Dispose"/>.
/// </summary>
internal sealed class PluginContainerSearch : IContainerSearch, IDisposable
{
    private readonly List<(LoadedPlugin Plugin, Plugin Instance, IReadOnlyList<string> Extensions)> _plugins = [];
    private readonly List<IDisposable> _busy = [];

    public PluginContainerSearch(PluginManager manager)
    {
        foreach (var plugin in manager.Loaded)
        {
            var extensions = PluginManager.Guard(plugin, () => plugin.Instance!.FileExtensions.ToList(), []);
            if (extensions.Count > 0 && plugin.Instance is { } instance)
            {
                _plugins.Add((plugin, instance, extensions));
                _busy.Add(manager.BeginOperation(plugin));
            }
        }
    }

    public bool IsEmpty => _plugins.Count == 0;

    public bool Claims(string fileName) => _plugins.Any(p => PluginFiles.Claims(p.Extensions, fileName));

    public IAsyncEnumerable<ContainerEntry>? Enumerate(string path, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(path);
        foreach (var (_, instance, extensions) in _plugins)
        {
            if (!PluginFiles.Claims(extensions, name))
            {
                continue;
            }

            if (instance.SearchFile(new FileSearchSource(path), cancellationToken) is { } entries)
            {
                return Convert(entries, cancellationToken);
            }
        }

        return null;
    }

    /// <summary>Returns the plugins to the manager; on the UI thread, as the manager is not thread-safe.</summary>
    public void Dispose()
    {
        foreach (var busy in _busy)
        {
            busy.Dispose();
        }

        _busy.Clear();
    }

    private static async IAsyncEnumerable<ContainerEntry> Convert(IAsyncEnumerable<SearchEntry> entries, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var entry in entries.WithCancellation(cancellationToken))
        {
            yield return new ContainerEntry(entry.InnerPath, entry.IsFolder, entry.Size, entry.Modified, entry.OpenAsync);
        }
    }
}
