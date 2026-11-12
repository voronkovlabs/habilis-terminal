using System.IO.Compression;
using System.Text.Json;

namespace HabilisTerminal.Core.Plugins;

/// <summary>Where a plugin came from: shipped ones can only be turned off, what the user installed can also be removed.</summary>
public enum PluginOrigin
{
    Shipped,
    User,
}

/// <summary>A plugin on disk, running or not.</summary>
public sealed record InstalledPlugin(string Id, string Name, string? Version, string Folder, PluginOrigin Origin, string? Error);

/// <summary>A plugin about to be installed: its files, unpacked if they came in a zip.</summary>
public sealed class PluginPackage(string folder, PluginManifest manifest, string? temporaryFolder) : IDisposable
{
    public string Folder { get; } = folder;

    public PluginManifest Manifest { get; } = manifest;

    public void Dispose()
    {
        if (temporaryFolder is not null && Directory.Exists(temporaryFolder))
        {
            try
            {
                Directory.Delete(temporaryFolder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A temporary folder: nothing depends on it going now.
            }
        }
    }
}

/// <summary>
/// The plugins on disk (issue #166, docs/plugins.md §3): what is installed, what is turned off, and installing and
/// removing. Knows nothing of what is running; the window loads and unloads.
/// </summary>
/// <param name="shippedRoot">The <c>plugins</c> folder next to the executable.</param>
/// <param name="userRoot">The user's plugins folder. In a portable copy it is the same folder as <paramref name="shippedRoot"/>.</param>
public sealed class PluginStore(string shippedRoot, string userRoot)
{
    /// <summary>In a plugin folder: the user installed it, for a portable copy, where both kinds share one folder.</summary>
    public const string UserInstalledMarker = ".user-installed";

    private const string DisabledFile = "disabled.json";

    public IReadOnlyList<string> Roots { get; } =
        new[] { shippedRoot, userRoot }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>What is on disk; the same id in both folders once, as it loads.</summary>
    public IReadOnlyList<InstalledPlugin> List() => [.. PluginLoader.Discover(Roots).Select(Read)];

    /// <summary>The plugin folders to load at startup: what is on disk, without what is turned off.</summary>
    public IReadOnlyList<string> FoldersToLoad()
    {
        var disabled = Disabled();
        return [.. List().Where(p => !disabled.Contains(p.Id)).Select(p => p.Folder)];
    }

    public bool IsDisabled(string id) => Disabled().Contains(id);

    /// <summary>Kept in the user's folder, so it outlives updates of the app.</summary>
    public void SetDisabled(string id, bool disabled)
    {
        var ids = Disabled();
        if (disabled ? !ids.Add(id) : !ids.Remove(id))
        {
            return;
        }

        Directory.CreateDirectory(userRoot);
        File.WriteAllText(Path.Combine(userRoot, DisabledFile), JsonSerializer.Serialize(ids.Order(StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>Removes what could not be deleted while it was loaded. Call before anything loads.</summary>
    public void Purge()
    {
        foreach (var root in Roots.Where(Directory.Exists))
        {
            foreach (var folder in Directory.GetDirectories(root).Where(f => File.Exists(Path.Combine(f, PluginLoader.RemoveMarker))))
            {
                DeleteMarked(folder);
            }
        }
    }

    /// <summary>Reads a zip or a folder holding a plugin, in it or in its only subfolder, and checks it.</summary>
    /// <exception cref="InvalidDataException">It is not a usable plugin; the message is for the user.</exception>
    /// <exception cref="IOException">It cannot be read.</exception>
    public static PluginPackage Open(string source, string temporaryRoot)
    {
        string? temporary = null;
        var folder = source;
        if (File.Exists(source))
        {
            if (!source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A plugin installs from a .zip file or a folder.");
            }

            temporary = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N")[..8]);
            folder = temporary;
        }
        else if (!Directory.Exists(source))
        {
            throw new InvalidDataException($"Not found: {source}");
        }

        try
        {
            if (temporary is not null)
            {
                Extract(source, temporary);
            }

            folder = FindManifestFolder(folder) ?? throw new InvalidDataException($"No {PluginManifest.FileName} in {source}.");
            var manifest = PluginManifest.Parse(File.ReadAllText(Path.Combine(folder, PluginManifest.FileName)));
            if (!File.Exists(Path.Combine(folder, manifest.Assembly)))
            {
                throw new InvalidDataException($"{manifest.Assembly}, named in {PluginManifest.FileName}, is missing.");
            }

            return new PluginPackage(folder, manifest, temporary);
        }
        catch when (temporary is not null)
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>Why this package cannot be installed, or null. A copy the shipped one would beat would never load.</summary>
    public string? Refusal(PluginPackage package)
    {
        if (IsInside(package.Folder, userRoot) || IsInside(package.Folder, shippedRoot))
        {
            return "This folder is already among the installed plugins.";
        }

        var shipped = ShippedCopy(package.Manifest.Id);
        return shipped is not null && VersionOf(shipped.Version) >= VersionOf(package.Manifest.Version)
            ? $"Version {shipped.Version} of \"{shipped.Name}\" comes with the app, and this one is {package.Manifest.Version}."
            : null;
    }

    /// <summary>Copies the plugin into the user's folder; returns where. What runs from an older copy keeps its folder until it is removed.</summary>
    /// <exception cref="IOException">It could not be copied; nothing stays.</exception>
    public string Install(PluginPackage package)
    {
        var target = Path.Combine(userRoot, package.Manifest.Id);
        if (Directory.Exists(target))
        {
            target += "." + Guid.NewGuid().ToString("N")[..8];
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(package.Folder, "*", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(target, Path.GetRelativePath(package.Folder, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }

            File.WriteAllText(Path.Combine(target, UserInstalledMarker), string.Empty);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>
    /// Removes a plugin the user installed. A plugin loaded at startup keeps its files locked, even after an unload: then
    /// false, it no longer counts as installed, and its files go at the next start.
    /// </summary>
    /// <exception cref="InvalidOperationException">The folder is not one the user installed: shipped plugins are only turned off.</exception>
    public bool Remove(string folder)
    {
        if (!Roots.Any(root => IsDirectChild(folder, root)) || OriginOf(folder) != PluginOrigin.User)
        {
            throw new InvalidOperationException($"{folder} is not a plugin the user installed.");
        }

        File.WriteAllText(Path.Combine(folder, PluginLoader.RemoveMarker), string.Empty);
        return DeleteMarked(folder);
    }

    /// <summary>A failed install: its files go, with no check of how it got there.</summary>
    public void Discard(string folder)
    {
        if (IsDirectChild(folder, userRoot))
        {
            TryDelete(folder);
        }
    }

    private InstalledPlugin? ShippedCopy(string id)
    {
        if (!Directory.Exists(shippedRoot))
        {
            return null;
        }

        return Directory.EnumerateDirectories(shippedRoot)
            .Where(f => File.Exists(Path.Combine(f, PluginManifest.FileName)) && !File.Exists(Path.Combine(f, PluginLoader.RemoveMarker)))
            .Select(Read)
            .FirstOrDefault(p => p.Origin == PluginOrigin.Shipped && string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private InstalledPlugin Read(string folder)
    {
        var name = Path.GetFileName(folder);
        try
        {
            var manifest = PluginManifest.Parse(File.ReadAllText(Path.Combine(folder, PluginManifest.FileName)));
            return new InstalledPlugin(manifest.Id, manifest.Name, manifest.Version, folder, OriginOf(folder), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new InstalledPlugin(name, name, null, folder, OriginOf(folder), ex.Message);
        }
    }

    private PluginOrigin OriginOf(string folder) =>
        IsDirectChild(folder, shippedRoot) && !File.Exists(Path.Combine(folder, UserInstalledMarker)) ? PluginOrigin.Shipped : PluginOrigin.User;

    private HashSet<string> Disabled()
    {
        try
        {
            var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(userRoot, DisabledFile)));
            return new HashSet<string>(ids ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Extract(string zip, string target)
    {
        try
        {
            // Refuses entries that would land outside the target.
            ZipFile.ExtractToDirectory(zip, target);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidDataException($"{Path.GetFileName(zip)} could not be unpacked: {ex.Message}", ex);
        }
    }

    private static string? FindManifestFolder(string folder)
    {
        if (File.Exists(Path.Combine(folder, PluginManifest.FileName)))
        {
            return folder;
        }

        var inner = Directory.GetDirectories(folder);
        return inner.Length == 1 && Directory.GetFiles(folder).Length == 0 && File.Exists(Path.Combine(inner[0], PluginManifest.FileName)) ? inner[0] : null;
    }

    private static Version VersionOf(string? text) => Version.TryParse(text, out var version) ? version : new Version(0, 0);

    private static bool IsDirectChild(string folder, string root) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd('\\')), Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string folder, string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>The marker goes last: a locked file stops the delete part way, and the next start must still find the folder.</summary>
    private static bool DeleteMarked(string folder)
    {
        var marker = Path.Combine(folder, PluginLoader.RemoveMarker);
        var complete = true;
        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Where(f => !string.Equals(f, marker, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
            }
        }

        return complete && TryDelete(folder);
    }

    private static bool TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
