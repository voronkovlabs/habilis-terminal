namespace HabilisTerminal.Core.Settings;

/// <summary>
/// Where settings, state and user themes live (SPEC §14): <c>%APPDATA%\HabilisTerminal.Avalonia</c> for settings and
/// themes, <c>%LOCALAPPDATA%\VoronkovLabs\HabilisTerminal.Avalonia</c> for state — or everything next to the executable in
/// portable mode (a <c>settings.json</c> or a <c>portable</c> marker beside it). The Avalonia build has folders of its own,
/// so it installs and runs next to the WPF build (issue #256); its first start copies the WPF build's settings.
/// </summary>
public sealed record AppPaths(string SettingsDirectory, string StateDirectory, bool IsPortable)
{
    public const string CompanyFolder = "VoronkovLabs";
    public const string ProductFolder = "HabilisTerminal.Avalonia";

    /// <summary>The WPF build's folder name, whose settings the first start copies.</summary>
    public const string WpfProductFolder = "HabilisTerminal";
    public const string PortableMarker = "portable";

    private static readonly string[] LegacyStateEntries = ["state.json", "crashes", "diagnostics", "plugins"];

    public string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    public string SchemaFile => Path.Combine(SettingsDirectory, "settings.schema.json");

    public string StateFile => Path.Combine(StateDirectory, "state.json");

    public string ThemesDirectory => Path.Combine(SettingsDirectory, "themes");

    /// <summary>Crash reports waiting to be offered on the next start.</summary>
    public string CrashDirectory => Path.Combine(StateDirectory, "crashes");

    /// <summary>Performance data waiting to be sent (issue #130).</summary>
    public string DiagnosticsDirectory => Path.Combine(StateDirectory, "diagnostics");

    /// <summary>The user's plugins, one folder each (docs/plugins.md §3); next to the executable when portable.</summary>
    public string PluginsDirectory => Path.Combine(StateDirectory, "plugins");

    /// <summary>What recent Alt+F7 text searches found (issue #211).</summary>
    public string SearchCacheDirectory => Path.Combine(StateDirectory, "search-cache");

    /// <summary>Plugin settings, one JSON file per plugin id.</summary>
    public string PluginSettingsDirectory => Path.Combine(SettingsDirectory, "plugin-settings");

    public static AppPaths Resolve(string executableDirectory) => Resolve(
        executableDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static AppPaths Resolve(string executableDirectory, string roamingAppData, string localAppData)
    {
        if (File.Exists(Path.Combine(executableDirectory, "settings.json")) || File.Exists(Path.Combine(executableDirectory, PortableMarker)))
        {
            return new AppPaths(executableDirectory, executableDirectory, IsPortable: true);
        }

        return new AppPaths(
            Path.Combine(roamingAppData, ProductFolder),
            Path.Combine(localAppData, CompanyFolder, ProductFolder),
            IsPortable: false);
    }

    /// <summary>
    /// Moves persistent local data out of Velopack's legacy package root. Velopack removes that root on uninstall;
    /// the separate company folder survives and is picked up after a later installation. Installed application files
    /// and disposable WebView2 data are deliberately not moved.
    /// </summary>
    public static void MigrateLegacyData(AppPaths paths) => MigrateLegacyData(
        paths,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static void MigrateLegacyData(AppPaths paths, string localAppData)
    {
        if (paths.IsPortable)
        {
            return;
        }

        var legacyDirectory = Path.Combine(localAppData, ProductFolder);
        foreach (var name in LegacyStateEntries)
        {
            TryMoveIfMissing(Path.Combine(legacyDirectory, name), Path.Combine(paths.StateDirectory, name));
        }
    }

    /// <summary>
    /// The first start of the Avalonia build takes the WPF build's settings, user themes and plugin settings, so both
    /// begin alike; afterwards each keeps its own. State (history, layout) is not copied.
    /// </summary>
    public static void CopyWpfSettings(AppPaths paths) =>
        CopyWpfSettings(paths, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    public static void CopyWpfSettings(AppPaths paths, string roamingAppData)
    {
        var wpf = Path.Combine(roamingAppData, WpfProductFolder);
        if (paths.IsPortable || File.Exists(paths.SettingsFile) || !Directory.Exists(wpf))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(paths.SettingsDirectory);
            if (File.Exists(Path.Combine(wpf, "settings.json")))
            {
                File.Copy(Path.Combine(wpf, "settings.json"), paths.SettingsFile);
            }

            foreach (var folder in new[] { "themes", "plugin-settings" })
            {
                var source = Path.Combine(wpf, folder);
                if (!Directory.Exists(source))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(paths.SettingsDirectory, folder, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings start from the defaults then.
        }
    }

    private static void TryMoveIfMissing(string source, string destination)
    {
        try
        {
            if (File.Exists(source))
            {
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(source, destination);
            }
            else if (Directory.Exists(source) && !File.Exists(destination) && !Directory.Exists(destination))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.Move(source, destination);
            }
        }
        catch (IOException)
        {
            // A failed migration must not keep the application from starting; the legacy data remains untouched.
        }
        catch (UnauthorizedAccessException)
        {
            // The old package root may already be protected or in the middle of an update.
        }
    }
}
