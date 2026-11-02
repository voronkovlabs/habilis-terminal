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
    /// <summary>Plugin folders (subfolders with a plugin.json) under the given roots; the first root wins for an id.</summary>
    public static IReadOnlyList<string> Discover(IEnumerable<string> roots)
    {
        var folders = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var folder in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(folder, PluginManifest.FileName)) && ids.Add(Path.GetFileName(folder)))
                {
                    folders.Add(folder);
                }
            }
        }

        return folders;
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
        private static readonly string SdkName = typeof(Plugin).Assembly.GetName().Name!;
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // The SDK is shared: plugin types must derive from the host's Plugin class.
            if (assemblyName.Name == SdkName)
            {
                return null;
            }

            return _resolver.ResolveAssemblyToPath(assemblyName) is { } path ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
            _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
