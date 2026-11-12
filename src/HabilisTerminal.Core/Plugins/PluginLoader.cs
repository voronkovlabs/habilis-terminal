using HabilisTerminal.Core.FileSystem;
using System.Reflection;
using System.Runtime.Loader;
using HabilisTerminal.Plugins;

namespace HabilisTerminal.Core.Plugins;

/// <summary>A discovered plugin: loaded (<see cref="Instance"/>) or failed (<see cref="Error"/>).</summary>
public sealed record LoadedPlugin(string Folder, PluginManifest? Manifest, Plugin? Instance, string? Error)
{
    public string Id => Manifest?.Id ?? Path.GetFileName(Source.TrimEndSeparator());

    public string Name => Manifest?.Name ?? Path.GetFileName(Source.TrimEndSeparator());

    /// <summary>Where a reload reads it from: <see cref="Folder"/> is a temporary copy for a plugin loaded through the API.</summary>
    public string Source { get => field ?? Folder; init; }

    /// <summary>Its load context, to unload it.</summary>
    public AssemblyLoadContext? Context { get; init; }

    /// <summary>The whole exception behind <see cref="Error"/>, for the plugin's developer.</summary>
    public string? ErrorDetail { get; init; }
}

/// <summary>
/// Finds plugin folders and loads each plugin into its own <see cref="AssemblyLoadContext"/>, so plugins bring their
/// own dependencies; the SDK assembly is always the host's (docs/plugins.md §3).
/// </summary>
public static class PluginLoader
{
    /// <summary>In a plugin folder: it was removed, but its files were locked. It no longer counts; the next start deletes it.</summary>
    public const string RemoveMarker = ".remove";

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
                if (!File.Exists(Path.Combine(folder, PluginManifest.FileName)) || File.Exists(Path.Combine(folder, RemoveMarker)))
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

    /// <summary>
    /// Copies a plugin folder to a new temporary folder, so that the original can be rebuilt while the plugin runs.
    /// </summary>
    /// <exception cref="IOException">The folder cannot be copied.</exception>
    public static string ShadowCopy(string folder, string root)
    {
        var target = Path.Combine(root, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), Guid.NewGuid().ToString("N")[..8]);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(folder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        return target;
    }

    /// <summary>Removes the temporary copies of apps that no longer run. One still in use stays.</summary>
    public static void CleanShadowCopies(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (int.TryParse(Path.GetFileName(folder), out var processId) && IsRunning(processId))
            {
                continue;
            }

            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still loaded: the next start tries again.
            }
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Starts unloading; the reference tells when the code has really left memory.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static WeakReference Unload(AssemblyLoadContext context)
    {
        var weak = new WeakReference(context);
        context.Unload();
        return weak;
    }

    /// <summary>Loads one plugin; never throws: failures come back in <see cref="LoadedPlugin.Error"/>.</summary>
    /// <param name="folder">Where it runs from.</param>
    /// <param name="source">Where a reload reads it from, when <paramref name="folder"/> is a temporary copy.</param>
    public static LoadedPlugin Load(string folder, string? source = null)
    {
        PluginManifest? manifest = null;
        AssemblyLoadContext? loadContext = null;
        try
        {
            manifest = PluginManifest.Parse(File.ReadAllText(Path.Combine(folder, PluginManifest.FileName)));
            var assemblyPath = Path.Combine(folder, manifest.Assembly);
            if (!File.Exists(assemblyPath))
            {
                return new LoadedPlugin(folder, manifest, null, $"{manifest.Assembly} is missing.") { Source = source ?? folder };
            }

            var context = new PluginLoadContext(manifest.Id, assemblyPath, manifest.Collectible);
            loadContext = context;
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var entries = assembly.GetExportedTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(Plugin).IsAssignableFrom(t))
                .ToList();
            if (entries.Count != 1)
            {
                return new LoadedPlugin(folder, manifest, null, entries.Count == 0
                    ? $"{manifest.Assembly} has no public class derived from Plugin."
                    : $"{manifest.Assembly} has {entries.Count} plugin classes; it must have exactly one.")
                { Source = source ?? folder, Context = context };
            }

            var instance = (Plugin)Activator.CreateInstance(entries[0])!;
            return new LoadedPlugin(folder, manifest, instance, null) { Source = source ?? folder, Context = context };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var inner = ex is TargetInvocationException { InnerException: { } cause } ? cause : ex;
            return new LoadedPlugin(folder, manifest, null, inner.Message) { Source = source ?? folder, Context = loadContext, ErrorDetail = inner.ToString() };
        }
    }

    private sealed class PluginLoadContext(string id, string assemblyPath, bool collectible) : AssemblyLoadContext($"plugin:{id}", collectible)
    {
        private static readonly Assembly SdkAssembly = typeof(Plugin).Assembly;
        private static readonly string SdkName = SdkAssembly.GetName().Name!;
        private const string SdkViewName = "HabilisTerminal.Plugins.Sdk.Avalonia";
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

            // The view part of the SDK (issue #78) and the UI framework under it (Avalonia, issue #253) are shared the same
            // way: the host's copies, by name, so a plugin's controls are the window's own types.
            if (assemblyName.Name is { } name && (name == SdkViewName || name.StartsWith("Avalonia", StringComparison.Ordinal)) &&
                Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name) is { } shared)
            {
                return shared;
            }

            if (assemblyName.Name == SdkViewName)
            {
                return Default.LoadFromAssemblyName(new AssemblyName(SdkViewName));
            }

            return _resolver.ResolveAssemblyToPath(assemblyName) is { } path ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
            _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
