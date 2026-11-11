using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using HabilisTerminal.App.Theming;
using HabilisTerminal.App.Updates;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Settings;
using HabilisTerminal.Shell;
using HabilisTerminal.Themes;

namespace HabilisTerminal.App;

/// <summary>
/// Application start: resolves where settings and state live, loads them, makes sure PowerShell 7 exists, then opens
/// the window and starts the background update check.
/// </summary>
public partial class App : Application
{
    private const string PowerShellDownloadPage = "https://aka.ms/powershell-release?tag=stable";

    public static AppPaths Paths { get; private set; } = null!;

    public static SettingsStore Settings { get; private set; } = null!;

    public static StateStore State { get; private set; } = null!;

    public static UpdateService Updates { get; } = new();

    /// <summary><c>shell.path</c> when it points to a file, otherwise PowerShell 7 found on this machine.</summary>
    public static string? FindPwsh()
    {
        var configured = Settings?.Current.ShellPath;
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? configured : PwshLocator.Find();
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        desktop.ShutdownRequested += (_, _) => OnExit();
        Reporting.CrashCapture.Install(this);
        Paths = AppPaths.Resolve(AppContext.BaseDirectory);
        AppPaths.MigrateLegacyData(Paths);
        Settings = new SettingsStore(Paths.SettingsFile);
        Settings.Load();
        SettingsStore.WriteSchema(Paths.SchemaFile);
        TryCreateDirectory(Paths.ThemesDirectory);
        State = new StateStore(Paths.StateFile);

        if (!EnsurePowerShell())
        {
            desktop.Shutdown();
            return;
        }

        var window = new MainWindow();
        desktop.MainWindow = window;
        window.Show();
        Updates.Start(Dispatcher.UIThread);
        OfferCrashReport(window);
        Reporting.CrashCapture.StartTestTrigger(Dispatcher.UIThread);
    }

    private static void OnExit()
    {
        Updates.ApplyPendingOnExit();
        Settings?.Dispose();
    }

    /// <summary>
    /// The pwsh-missing screen (SPEC §15): explains how to install PowerShell 7 and offers to check again.
    /// Returns false when the user gives up.
    /// </summary>
    private static bool EnsurePowerShell()
    {
        if (FindPwsh() is not null)
        {
            return true;
        }

        PanelPalette.Apply(ThemeLoader.LoadBuiltIn(WindowsAppearance.BuiltInThemeFor(WindowsAppearance.IsHighContrast())));
        while (true)
        {
            var body = new StackPanel { MaxWidth = 480 };
            var text = new TextBlock
            {
                Text = "Habilis Terminal runs PowerShell 7 (pwsh.exe) in its terminal, and it was not found on this computer.\n\n" +
                       "Install it with winget, or download it from Microsoft, then choose Check again.",
                TextWrapping = TextWrapping.Wrap,
            };
            text.Classes.Add("label");
            var command = new TextBox { Text = "winget install Microsoft.PowerShell", IsReadOnly = true, Margin = new Thickness(0, 12, 0, 0), FontFamily = new FontFamily("Cascadia Mono, Consolas") };
            body.Children.Add(text);
            body.Children.Add(command);

            var choice = ThemedDialog.Show(null, "PowerShell 7 is required", body,
            [
                new DialogButton<int>("_Check again", 1, IsDefault: true),
                new DialogButton<int>("Copy _command", 2),
                new DialogButton<int>("_Download page", 3),
                new DialogButton<int>("E_xit", 0, IsCancel: true),
            ], 0);

            switch (choice)
            {
                case 0:
                    return false;
                case 2:
                    try
                    {
                        Widgets.Clipboard.SetText(command.Text ?? string.Empty);
                    }
                    catch (System.Runtime.InteropServices.ExternalException)
                    {
                    }

                    break;
                case 3:
                    OpenInBrowser(PowerShellDownloadPage);
                    break;
            }

            if (FindPwsh() is not null)
            {
                return true;
            }
        }
    }

    /// <summary>A crash report written last time is offered once the window is up (issue #22).</summary>
    private static void OfferCrashReport(Window window)
    {
        var pending = Core.Reporting.CrashReport.LoadPending(Paths.CrashDirectory);
        if (pending.Count > 0)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Reporting.ReportDialog.OfferCrash(window, pending), DispatcherPriority.ApplicationIdle);
        }
    }

    public static void OpenInBrowser(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No browser registered: nothing else to try.
        }
    }

    private static void TryCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
