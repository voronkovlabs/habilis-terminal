using System.IO;
using System.Windows;
using System.Windows.Input;
using HabilisTerminal.App.Input;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Theming;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Panels;

namespace HabilisTerminal.App;

/// <summary>F9 top menu, F1 help, Alt+F11 history and F10 exit (SPEC §5).</summary>
public partial class MainWindow
{
    private const string LeftMenu = "Left";
    private const string RightMenu = "Right";

    /// <summary>The menu headers in the title bar, in the order of <see cref="ShowTopMenuAsync"/>'s groups.</summary>
    private static readonly string[] MenuTitles = [LeftMenu, "Files", "Commands", "Options", RightMenu];

    /// <summary>F9 opens on the menu of the active panel's side, like Far; a click on a title bar header opens that one.</summary>
    private async Task ShowTopMenuAsync(int? group = null)
    {
        var groups = new List<MenuGroup>
        {
            PanelMenu(LeftMenu, _left),
            new("Files",
            [
                MenuRow("files.view"),
                MenuRow("files.edit"),
                MenuRow("files.new"),
                MenuRow("files.copy"),
                MenuRow("files.rename"),
                MenuRow("files.move"),
                MenuRow("files.mkdir"),
                MenuRow("files.delete"),
                MenuRow("files.deleteNow"),
                MenuCommand.Separator,
                MenuRow("files.attributes"),
                MenuCommand.Separator,
                MenuRow("select.group"),
                MenuRow("select.ungroup"),
                MenuRow("select.invert"),
            ]),
            new("Commands",
            [
                MenuRow("files.find"),
                MenuRow("go.folderHistory"),
                MenuRow("go.fileHistory"),
                MenuCommand.Separator,
                MenuRow("panels.swap"),
                MenuRow("panels.toggle"),
                MenuRow("tabs.new"),
                MenuRow("tabs.next"),
                MenuRow("panels.inactive"),
                MenuRow("panel.compare"),
                MenuCommand.Separator,
                MenuRow("plugins.menu"),
                MenuRow("plugins.panelCommands"),
            ]),
            new("Options",
            [
                MenuRow("settings.open"),
                MenuRow("settings.file"),
                MenuCommand.Separator,
                MenuRow("settings.theme"),
                MenuRow("settings.iconTheme"),
                MenuRow("plugins.configure"),
                MenuCommand.Separator,
                MenuRow("window.zoomIn"),
                MenuRow("window.zoomOut"),
                MenuRow("window.zoomReset"),
                MenuCommand.Separator,
                MenuRow("window.help"),
                MenuRow("app.feedback"),
                MenuRow("app.performance"),
                MenuRow("app.updates"),
                MenuRow("app.about"),
            ]),
            PanelMenu(RightMenu, _right),
        };

        var result = await TopMenu.ShowAsync(_titleBar.MenuStrip, groups, group ?? (_active == _right ? groups.Count - 1 : 0));
        if (result?.Run is { } run)
        {
            run();
        }
        else if (!IsPageOpen)
        {
            FocusActivePanel();
        }
    }

    private MenuGroup PanelMenu(string title, FilePanel panel)
    {
        var mode = panel.MenuViewMode;
        MenuCommand View(string id, PanelViewMode viewMode) => PanelRow(panel, id, mode == viewMode);

        return new MenuGroup(title,
        [
            View("view.brief", PanelViewMode.Brief),
            View("view.medium", PanelViewMode.Medium),
            View("view.full", PanelViewMode.Full),
            View("view.wide", PanelViewMode.Wide),
            View("view.detailed", PanelViewMode.Detailed),
            View("view.alternative", PanelViewMode.AlternativeFull),
            MenuCommand.Separator,
            MenuRow("panel.info", () => PanelKey(Other(panel), Key.L, KeyModifiers.Control), Other(panel).InfoSource == panel),
            MenuRow("panel.quickView", () => PanelKey(Other(panel), Key.Q, KeyModifiers.Control), Other(panel).QuickViewSource == panel),
            MenuCommand.Separator,
            PanelRow(panel, "sort.menu"),
            PanelRow(panel, "panel.hidden", panel.State.ShowHidden),
            PanelRow(panel, "panel.reread"),
            MenuRow(panel == _left ? "go.driveLeft" : "go.driveRight"),
        ]);
    }

    /// <summary>Runs a panel key as if it was pressed in <paramref name="panel"/>, so the menu and the keys never disagree.</summary>
    private void PanelKey(FilePanel panel, Key key, KeyModifiers modifiers)
    {
        if (!IsShown(panel))
        {
            panel = Other(panel);
        }

        Activate(panel);
        SyncShellDirectory();
        FocusActivePanel();
        Dispatcher.BeginInvoke(() =>
        {
            if (!HandlePluginKey(panel, key, modifiers) && !HandleWindowKey(panel, key, modifiers))
            {
                panel.HandleKey(key, modifiers);
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private async Task PickThemeAsync()
    {
        var items = _themes.AvailableThemes().Select(name => new QuickPickItem { Label = name, Tag = name, IsCurrent = string.Equals(name, _settings.Theme, StringComparison.OrdinalIgnoreCase) }).ToList();
        var result = await QuickPick.ShowAsync(Workspace, items, new QuickPickOptions { Title = "Color theme", Placeholder = "Select a color theme" });
        FocusActivePanel();
        if (result?.Item?.Tag is string theme)
        {
            ChangeSetting("theme", theme);
        }
    }

    private async Task PickIconThemeAsync()
    {
        var items = Themes.FileIconTheme.BuiltInThemes.Select(name => new QuickPickItem { Label = name, Tag = name, IsCurrent = name == _settings.IconTheme }).ToList();
        var result = await QuickPick.ShowAsync(Workspace, items, new QuickPickOptions { Title = "File icon theme", Placeholder = "Select a file icon theme" });
        FocusActivePanel();
        if (result?.Item?.Tag is string theme)
        {
            ChangeSetting("iconTheme", theme);
        }
    }

    /// <summary>Writes one setting to settings.json and applies it at once (the file watcher would also pick it up).</summary>
    private void ChangeSetting(string key, object? value)
    {
        if (App.Settings is not { } store)
        {
            return;
        }

        try
        {
            ApplySettings(store.Set(key, value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            OperationDialogs.Message(this, "Settings", $"settings.json could not be changed: {ex.Message}");
        }
    }

    /// <summary>Alt+F11: files opened in the viewer or editor, most recent first; Enter opens them the same way again.</summary>
    private async Task ShowFileHistoryAsync()
    {
        var panel = _active;
        var items = _fileHistory.Select(entry => new QuickPickItem
        {
            Label = Path.GetFileName(entry.Path),
            Description = (entry.Edited ? "Edit · " : "View · ") + entry.Path,
            Icon = Codicon.Document,
            Tag = entry,
        }).ToList();

        var result = await QuickPick.ShowAsync(panel, items, new QuickPickOptions { Title = "View and edit history", Placeholder = "Filter files" });
        if (result?.Item?.Tag is not Core.Settings.FileHistoryEntry chosen)
        {
            FocusActivePanel();
            return;
        }

        if (!File.Exists(chosen.Path))
        {
            panel.Message = $"'{chosen.Path}' no longer exists.";
            FocusActivePanel();
            return;
        }

        if (chosen.Edited)
        {
            Edit(chosen.Path, panel);
        }
        else
        {
            OpenViewer(chosen.Path, panel);
        }
    }

    /// <summary>F10: exit, asking first when <c>confirmExit</c> is on (Far).</summary>
    private void ConfirmAndExit()
    {
        if (_settings.ConfirmExit)
        {
            var body = new System.Windows.Controls.TextBlock { Text = "Do you want to quit Habilis Terminal?" };
            body.SetResourceReference(StyleProperty, "Habi.LabelStyle");
            var quit = ThemedDialog.Show(this, "Exit", body, [new DialogButton<bool>("_Yes", true, IsDefault: true), new DialogButton<bool>("_No", false, IsCancel: true)], false);
            if (!quit)
            {
                FocusActivePanel();
                return;
            }
        }

        Close();
    }

    /// <summary>F9, Options (issue #130). Measuring is off by default, so say how to turn it on.</summary>
    private async Task SendPerformanceReportAsync()
    {
        if (!Reporting.PerfDiagnostics.Running)
        {
            Toasts.Notify("performance-report", "Performance diagnostics are off", "Turn on diagnostics.performance in Settings, work for a while, then send the report.");
            return;
        }

        var result = await Reporting.PerfDiagnostics.SendNowAsync();
        var (title, text) = result switch
        {
            null => ("Nothing to send yet", "No measurements have been collected since the last report."),
            { Link: { } link } => ("Performance report sent", link.Url),
            { OpenedInBrowser: true } => ("Performance report", "This build cannot send by itself: the report opened in the browser. The data stays here until a report is sent."),
            _ => ("Performance report not sent", (result.Error ?? "Unknown error.") + " The data stays here for the next attempt."),
        };
        Toasts.Notify("performance-report", title, text);
    }

    private void OnUpdateReady(string version) =>
        Toasts.Notify("update", "Update ready", $"Habilis Terminal {version} has been downloaded. It is installed when you exit, or now:",
            ("Restart to update", RestartToUpdate));

    /// <summary>F9 > Options > Check for updates (issue #71): the answer in a notice.</summary>
    private async Task CheckForUpdatesAsync()
    {
        Toasts.Notify("update", "Checking for updates…", $"Habilis Terminal {Updates.UpdateService.RunningVersion}");
        var check = await App.Updates.CheckNowAsync();
        switch (check.Kind)
        {
            case Updates.UpdateCheckKind.Ready:
                OnUpdateReady(check.Version);
                break;
            case Updates.UpdateCheckKind.Available:
                Toasts.Notify("update", "Update available",
                    $"Habilis Terminal {check.Version} is available. This copy is {(App.Paths.IsPortable ? "portable" : "a development build")}, so it does not update itself: download the new version from the releases page.",
                    ("Open releases page", () => App.OpenInBrowser(Updates.UpdateService.ReleasesUrl)));
                break;
            case Updates.UpdateCheckKind.UpToDate:
                Toasts.Notify("update", "No updates", $"Habilis Terminal {check.Version} is the latest version.");
                break;
            default:
                Toasts.Notify("update", "Cannot check for updates", check.Problem ?? "The check failed.",
                    ("Open releases page", () => App.OpenInBrowser(Updates.UpdateService.ReleasesUrl)));
                break;
        }
    }

    private void RestartToUpdate()
    {
        if (PageHost.Content is Editing.EditorPage editor && !editor.ConfirmLeave())
        {
            return;
        }

        SaveState();
        App.Updates.RestartToUpdate();
    }

    private void ShowAbout()
    {
        var version = typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "?";
        var mode = App.Updates.InstalledVersion is not null ? "installed, updates from GitHub Releases"
            : App.Paths.IsPortable ? "portable" : "development build";
        var body = new System.Windows.Controls.StackPanel { MaxWidth = 520 };
        void Line(string text, string style = "Habi.LabelStyle")
        {
            var block = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            block.SetResourceReference(StyleProperty, style);
            body.Children.Add(block);
        }

        Line($"Version {version} ({mode})");
        Line("© 2026 Sergei Voronkov, Voronkov Labs");
        Line($".NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}", "Habi.DescriptionStyle");
        Line($"Shell: {App.FindPwsh()}", "Habi.DescriptionStyle");
        Line($"Settings: {App.Paths.SettingsFile}", "Habi.DescriptionStyle");
        Line("MIT License. Includes VS Code themes and Seti icons (MIT), Material Icon Theme (MIT), Codicons (CC BY 4.0), " +
             "the Windows Terminal control and ConPTY (MIT), AvalonEdit (MIT), Velopack (MIT), Markdig (BSD-2-Clause) and the WebView2 SDK (BSD-3-Clause).", "Habi.DescriptionStyle");

        var choice = ThemedDialog.Show(this, "Habilis Terminal", body,
        [
            new DialogButton<int>("_OK", 0, IsDefault: true, IsCancel: true),
            new DialogButton<int>("Third-party _notices", 1),
            new DialogButton<int>("_GitHub", 2),
            new DialogButton<int>("_LinkedIn", 3),
        ], 0);

        var notices = Path.Combine(AppContext.BaseDirectory, "ThirdPartyNotices.txt");
        switch (choice)
        {
            case 1 when File.Exists(notices):
                OpenViewer(notices, _active);
                break;
            case 2:
                App.OpenInBrowser(Updates.UpdateService.RepositoryUrl);
                FocusActivePanel();
                break;
            case 3:
                App.OpenInBrowser("https://www.linkedin.com/in/sergei-v-voronkov");
                FocusActivePanel();
                break;
            default:
                FocusActivePanel();
                break;
        }
    }

    /// <summary>The feedback dialog (title bar icon, F9 > Options), issue #23.</summary>
    private void ShowFeedback()
    {
        Reporting.ReportDialog.ShowFeedback(this);
        if (!IsPageOpen)
        {
            FocusActivePanel();
        }
    }

    private void ShowHelp()
    {
        var page = new Help.HelpPage();
        page.CloseRequested += () => ClosePage();
        ShowPage(page);
    }

    private void OpenSettingsPage()
    {
        if (App.Settings is not { } store)
        {
            return;
        }

        var page = new SettingsUi.SettingsPage(store, _themes.AvailableThemes());
        page.CloseRequested += () => ClosePage();
        page.ValueChanged += ChangeSetting;
        page.OpenJsonRequested += OpenSettingsFile;
        ShowPage(page);
    }
}
