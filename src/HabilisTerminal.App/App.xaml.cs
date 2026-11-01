using System.IO;
using System.Windows;
using HabilisTerminal.Core.Settings;

namespace HabilisTerminal.App;

/// <summary>Application start: resolves where settings and state live and loads them before the window opens.</summary>
public partial class App : Application
{
    public static AppPaths Paths { get; private set; } = null!;

    public static SettingsStore Settings { get; private set; } = null!;

    public static StateStore State { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        Paths = AppPaths.Resolve(AppContext.BaseDirectory);
        Settings = new SettingsStore(Paths.SettingsFile);
        Settings.Load();
        SettingsStore.WriteSchema(Paths.SchemaFile);
        Directory.CreateDirectory(Paths.ThemesDirectory);
        State = new StateStore(Paths.StateFile);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Settings?.Dispose();
        base.OnExit(e);
    }
}
