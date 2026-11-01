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

    /// <summary>F9: opens on the menu of the active panel's side, like Far.</summary>
    private async Task ShowTopMenuAsync()
    {
        var groups = new List<MenuGroup>
        {
            PanelMenu(LeftMenu, _left),
            new("Files",
            [
                Command("View", "F3", Key.F3),
                Command("Edit", "F4", Key.F4),
                Command("Copy", "F5", Key.F5),
                Command("Rename or move", "F6", Key.F6),
                Command("Make folder", "F7", Key.F7),
                Command(_settings.DeleteToRecycleBin ? "Delete to Recycle Bin" : "Delete", "F8", Key.F8),
                Command("Delete permanently", "Shift+Del", Key.Delete, KeyModifiers.Shift),
                MenuCommand.Separator,
                Command("Attributes", "Ctrl+A", Key.A, KeyModifiers.Control),
                MenuCommand.Separator,
                Command("Select group", "Gray +", Key.Add),
                Command("Deselect group", "Gray −", Key.Subtract),
                Command("Invert selection", "Gray *", Key.Multiply),
            ]),
            new("Commands",
            [
                Command("Folder history", "Alt+F12", Key.F12, KeyModifiers.Alt),
                Command("View and edit history", "Alt+F11", Key.F11, KeyModifiers.Alt),
                MenuCommand.Separator,
                Command("Swap panels", "Ctrl+U", Key.U, KeyModifiers.Control),
                new MenuCommand("Panels on / off", "Ctrl+O", TogglePanels),
                new MenuCommand("Compare folders", null, CompareFolders),
            ]),
            new("Options",
            [
                new MenuCommand("Settings", null, OpenSettingsPage),
                new MenuCommand("Open settings.json", null, OpenSettingsFile),
                MenuCommand.Separator,
                new MenuCommand("Color theme…", null, () => Fire(PickThemeAsync())),
                new MenuCommand("File icon theme…", null, () => Fire(PickIconThemeAsync())),
                MenuCommand.Separator,
                new MenuCommand("Keyboard help", "F1", ShowHelp),
                new MenuCommand("About Habilis Terminal", null, ShowAbout),
            ]),
            PanelMenu(RightMenu, _right),
        };

        var result = await TopMenu.ShowAsync(Workspace, groups, _active == _right ? groups.Count - 1 : 0);
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
        var mode = panel.State.ViewMode;
        MenuCommand View(string label, string shortcut, Key key, PanelViewMode viewMode) =>
            new(label, shortcut, () => PanelKey(panel, key, KeyModifiers.Control), mode == viewMode);

        return new MenuGroup(title,
        [
            View("Brief", "Ctrl+1", Key.D1, PanelViewMode.Brief),
            View("Medium", "Ctrl+2", Key.D2, PanelViewMode.Medium),
            View("Full", "Ctrl+3", Key.D3, PanelViewMode.Full),
            View("Wide", "Ctrl+4", Key.D4, PanelViewMode.Wide),
            View("Detailed", "Ctrl+5", Key.D5, PanelViewMode.Detailed),
            View("Alternative full", "Ctrl+0", Key.D0, PanelViewMode.AlternativeFull),
            MenuCommand.Separator,
            new("Info panel", "Ctrl+L", () => PanelKey(Other(panel), Key.L, KeyModifiers.Control), Other(panel).InfoSource == panel),
            new("Quick view", "Ctrl+Q", () => PanelKey(Other(panel), Key.Q, KeyModifiers.Control), Other(panel).QuickViewSource == panel),
            MenuCommand.Separator,
            new("Sort by…", "Ctrl+F12", () => PanelKey(panel, Key.F12, KeyModifiers.Control)),
            new("Show hidden files", "Ctrl+H", () => PanelKey(panel, Key.H, KeyModifiers.Control), panel.State.ShowHidden),
            new("Re-read", "Ctrl+R", () => PanelKey(panel, Key.R, KeyModifiers.Control)),
            new("Change drive", panel == _left ? "Alt+F1" : "Alt+F2", () => Fire(ShowDriveMenuAsync(panel))),
        ]);
    }

    /// <summary>A Files / Commands entry: the same key on the active panel.</summary>
    private MenuCommand Command(string label, string shortcut, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        new(label, shortcut, () => PanelKey(_active, key, modifiers));

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
            if (!HandleWindowKey(panel, key, modifiers))
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

    private void OnUpdateReady(string version) =>
        Toasts.Notify("update", "Update ready", $"Habilis Terminal {version} has been downloaded. It is installed when you exit, or now:",
            ("Restart to update", RestartToUpdate));

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
        Line($".NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}", "Habi.DescriptionStyle");
        Line($"Shell: {App.FindPwsh()}", "Habi.DescriptionStyle");
        Line($"Settings: {App.Paths.SettingsFile}", "Habi.DescriptionStyle");
        Line("MIT License. Includes VS Code themes and Seti icons (MIT), Material Icon Theme (MIT), Codicons (CC BY 4.0), " +
             "the Windows Terminal control and ConPTY (MIT), AvalonEdit (MIT) and Velopack (MIT).", "Habi.DescriptionStyle");

        var choice = ThemedDialog.Show(this, "Habilis Terminal", body,
        [
            new DialogButton<int>("_OK", 0, IsDefault: true, IsCancel: true),
            new DialogButton<int>("Third-party _notices", 1),
            new DialogButton<int>("_GitHub", 2),
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
            default:
                FocusActivePanel();
                break;
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
