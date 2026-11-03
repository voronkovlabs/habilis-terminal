using System.Reflection;
using System.Runtime.Loader;
using HabilisTerminal.Plugins;

namespace HabilisTerminal.Core.Plugins;

/// <summary>A discovered plugin: loaded (<see cref="Instance"/>) or failed (<see cref="Error"/>).</summary>
public sealed record LoadedPlugin(string Folder, PluginManifest? Manifest, Plugin? Instance, string? Error)
{
    public string Id => Manifest?.Id ?? Path.GetFileName(Folder);

    public string Name => Manifest?.Name ?? Path.GetFileName(Folder);
}

/// <summary>
/// Finds plugin folders and loads each plugin into its own <see cref="AssemblyLoadContext"/>, so plugins bring their
/// own dependencies; the SDK assembly is always the host's (docs/plugins.md §3).
/// </summary>
public static class PluginLoader
{
    /// <summary>
    /// Plugin folders (subfolders with a plugin.json) under the given roots. The same plugin in several roots (a standard
    /// plugin shipped with the app and a copy the user installed) loads once: the highest version wins, and on a tie the
    /// earlier root (issue #58). A folder whose manifest cannot be read is kept, so its error is shown.
    /// </summary>
    public static IReadOnlyList<string> Discover(IEnumerable<string> roots)
    {
        var chosen = new Dictionary<string, (string Folder, Version Version)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var folder in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(folder, PluginManifest.FileName)))
                {
                    continue;
                }

                var (id, version) = Identify(folder);
                if (!chosen.TryGetValue(id, out var current))
                {
                    order.Add(id);
                    chosen[id] = (folder, version);
                }
                else if (version > current.Version)
                {
                    chosen[id] = (folder, version);
                }
            }
        }

        return order.Select(id => chosen[id].Folder).ToList();
    }

    /// <summary>The manifest's id and version; the folder name and 0.0 when the manifest is unreadable.</summary>
    private static (string Id, Version Version) Identify(string folder)
    {
        try
        {
            var manifest = PluginManifest.Parse(File.ReadAllText(Path.Combine(folder, PluginManifest.FileName)));
            return (manifest.Id, Version.TryParse(manifest.Version, out var version) ? version : new Version(0, 0));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (Path.GetFileName(folder), new Version(0, 0));
        }
    }

    /// <summary>Loads one plugin; never throws: failures come back in <see cref="LoadedPlugin.Error"/>.</summary>
    public static LoadedPlugin Load(string folder)
    {
        PluginManifest? manifest = null;
        try
        {
            manifest = PluginManifest.Parse(File.ReadAllText(Path.Combine(folder, PluginManifest.FileName)));
            var assemblyPath = Path.Combine(folder, manifest.Assembly);
            if (!File.Exists(assemblyPath))
            {
                return new LoadedPlugin(folder, manifest, null, $"{manifest.Assembly} is missing.");
            }

            var context = new PluginLoadContext(manifest.Id, assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var entries = assembly.GetExportedTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(Plugin).IsAssignableFrom(t))
                .ToList();
            if (entries.Count != 1)
            {
                return new LoadedPlugin(folder, manifest, null, entries.Count == 0
                    ? $"{manifest.Assembly} has no public class derived from Plugin."
                    : $"{manifest.Assembly} has {entries.Count} plugin classes; it must have exactly one.");
            }

            var instance = (Plugin)Activator.CreateInstance(entries[0])!;
            return new LoadedPlugin(folder, manifest, instance, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var inner = ex is TargetInvocationException { InnerException: { } cause } ? cause : ex;
            return new LoadedPlugin(folder, manifest, null, inner.Message);
        }
    }

    private sealed class PluginLoadContext(string id, string assemblyPath) : AssemblyLoadContext($"plugin:{id}")
    {
        private static readonly Assembly SdkAssembly = typeof(Plugin).Assembly;
        private static readonly string SdkName = SdkAssembly.GetName().Name!;
        private const string SdkWpfName = "HabilisTerminal.Plugins.Sdk.Wpf";
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // The SDK is shared: plugin types must derive from the host's Plugin class.
            if (assemblyName.Name == SdkName)
            {
                // The host's own SDK assembly, whatever version the plugin was built against: binding by version would
                // refuse a plugin built against a newer minor SDK, and compatibility is already checked through the
                // manifest's "sdk" (issue #60).
                return SdkAssembly;
            }

            // The WPF part of the SDK (issue #78) is shared the same way: the host's copy, by name.
            if (assemblyName.Name == SdkWpfName)
            {
                return Default.LoadFromAssemblyName(new AssemblyName(SdkWpfName));
            }

            return _resolver.ResolveAssemblyToPath(assemblyName) is { } path ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
            _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
