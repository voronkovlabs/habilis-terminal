using System.IO;
using HabilisTerminal.Core.Plugins;
using HabilisTerminal.Core.Settings;
using HabilisTerminal.Plugins;

namespace HabilisTerminal.App.Plugins;

/// <summary>
/// The loaded plugins and their entry points: menus, prefixes, configuration (docs/plugins.md §3, §5). Every call into
/// a plugin goes through <see cref="Guard{T}"/>, so a failing plugin never takes the host down.
/// </summary>
internal sealed class PluginManager
{
    private readonly List<LoadedPlugin> _plugins = [];

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    public IEnumerable<LoadedPlugin> Loaded => _plugins.Where(p => p.Instance is not null);

    /// <summary>Plugin roots: <c>plugins</c> next to the executable (portable and test copies), then the user's folder.</summary>
    public static IReadOnlyList<string> Roots(AppPaths paths) =>
        new[] { Path.Combine(AppContext.BaseDirectory, "plugins"), paths.PluginsDirectory }
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Loads and initialises every plugin; failures are kept with their reason.</summary>
    public void LoadAll(AppPaths paths, Func<LoadedPlugin, IPluginHost> hostFor)
    {
        foreach (var folder in PluginLoader.Discover(Roots(paths)))
        {
            var loaded = PluginLoader.Load(folder);
            if (loaded is { Instance: { } plugin })
            {
                try
                {
                    plugin.Initialize(hostFor(loaded));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    loaded = loaded with { Instance = null, Error = $"Initialisation failed: {ex.Message}" };
                }
            }

            _plugins.Add(loaded);
        }
    }

    /// <summary>F11 items of all plugins.</summary>
    public IReadOnlyList<(LoadedPlugin Plugin, PluginMenuItem Item)> MenuItems() =>
        Loaded.SelectMany(p => Guard(p, () => p.Instance!.MenuItems.ToList(), []).Select(i => (p, i))).ToList();

    /// <summary>Drive-menu items of all plugins.</summary>
    public IReadOnlyList<(LoadedPlugin Plugin, PluginMenuItem Item)> DriveMenuItems() =>
        Loaded.SelectMany(p => Guard(p, () => p.Instance!.DriveMenuItems.ToList(), []).Select(i => (p, i))).ToList();

    /// <summary>The plugin claiming the address's prefix ("s3:..." → the plugin with prefix "s3"), or null.</summary>
    public LoadedPlugin? ForAddress(string address)
    {
        var colon = address.IndexOf(':', StringComparison.Ordinal);
        if (colon < 1)
        {
            return null;
        }

        var prefix = address[..colon];
        return Loaded.FirstOrDefault(p => Guard(p, () => p.Instance!.Prefixes.ToList(), []).Any(x => string.Equals(x, prefix, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>All registered prefixes (for the command-line hook).</summary>
    public IReadOnlyList<string> Prefixes() =>
        Loaded.SelectMany(p => Guard(p, () => p.Instance!.Prefixes.ToList(), [])).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The name ends in an extension some plugin opens as a folder (issue #148).</summary>
    public bool OpensAsFolder(string fileName) =>
        Loaded.Any(p => Guard(p, () => PluginFiles.Claims(p.Instance!.FileExtensions, fileName), false));

    /// <summary>
    /// Who to ask about a file: those claiming its name; with <paramref name="anyFormat"/> then every other plugin that
    /// opens files, as the content decides there.
    /// </summary>
    public IReadOnlyList<LoadedPlugin> ForFile(string fileName, bool anyFormat)
    {
        var withFiles = Loaded.Select(p => (Plugin: p, Extensions: Guard(p, () => p.Instance!.FileExtensions.ToList(), []))).Where(p => p.Extensions.Count > 0).ToList();
        var claiming = withFiles.Where(p => PluginFiles.Claims(p.Extensions, fileName)).Select(p => p.Plugin).ToList();
        return anyFormat ? [.. claiming, .. withFiles.Select(p => p.Plugin).Except(claiming)] : claiming;
    }

    /// <summary>What each plugin catches in the console, as "command prefix": "cd s3" (issue #140).</summary>
    public IReadOnlyList<string> AddressCommands() =>
        Loaded.SelectMany(p => Guard(p, () => p.Instance!.AddressCommands.SelectMany(c => p.Instance!.Prefixes.Select(x => $"{c} {x}")).ToList(), []))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Runs a synchronous plugin call; an exception counts as "nothing" (the plugin's menu just stays empty).</summary>
    public static T Guard<T>(LoadedPlugin plugin, Func<T> call, T fallback)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine($"Plugin {plugin.Id} failed: {ex}");
            return fallback;
        }
    }

    /// <summary>The message for a failed plugin call: the plugin's own text, or "Plugin X failed: ...".</summary>
    public static string Describe(LoadedPlugin plugin, Exception ex) =>
        ex is PluginException ? ex.Message : $"{plugin.Name} failed: {ex.Message}";
}
