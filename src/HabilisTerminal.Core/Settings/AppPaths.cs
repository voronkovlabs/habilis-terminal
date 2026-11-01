namespace HabilisTerminal.Core.Settings;

/// <summary>
/// Where settings, state and user themes live (SPEC §14): <c>%APPDATA%\HabilisTerminal</c> for settings and themes,
/// <c>%LOCALAPPDATA%\HabilisTerminal</c> for state — or everything next to the executable in portable mode
/// (a <c>settings.json</c> or a <c>portable</c> marker beside it).
/// </summary>
public sealed record AppPaths(string SettingsDirectory, string StateDirectory, bool IsPortable)
{
    public const string ProductFolder = "HabilisTerminal";
    public const string PortableMarker = "portable";

    public string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    public string SchemaFile => Path.Combine(SettingsDirectory, "settings.schema.json");

    public string StateFile => Path.Combine(StateDirectory, "state.json");

    public string ThemesDirectory => Path.Combine(SettingsDirectory, "themes");

    /// <summary>Crash reports waiting to be offered on the next start.</summary>
    public string CrashDirectory => Path.Combine(StateDirectory, "crashes");

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

        return new AppPaths(Path.Combine(roamingAppData, ProductFolder), Path.Combine(localAppData, ProductFolder), IsPortable: false);
    }
}
