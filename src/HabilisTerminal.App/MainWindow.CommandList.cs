using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Input;
using HabilisTerminal.App.Input;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Plugins;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Commands;
using HabilisTerminal.Core.Panels;
using HabilisTerminal.Core.Plugins;
using HabilisTerminal.Plugins;

namespace HabilisTerminal.App;

/// <summary>
/// The command line in the title bar (issue #155), as Ctrl+Shift+P in VS Code. The commands are
/// <see cref="CommandCatalog"/> and those of the plugins; each runs as its key does.
/// </summary>
public partial class MainWindow
{
    private const int RecentCommandCapacity = 10;

    private readonly List<string> _recentCommands = [];
    private bool _commandLineOpen;

    /// <summary>What a command applies to, as it stands now.</summary>
    private CommandContext CaptureCommandContext()
    {
        var panel = _active;
        var state = panel.State;
        var cursor = !IsShown(panel) || state.Count == 0 ? CursorKind.None
            : state.Current.IsParent ? CursorKind.Parent
            : state.Current.Entry.IsDirectory ? CursorKind.Folder
            : CursorKind.File;
        var other = Other(panel);
        return new CommandContext
        {
            Area = PageHost.Content switch
            {
                Viewing.ViewerPage => CommandArea.Viewer,
                Editing.EditorPage => CommandArea.Editor,
                null => TerminalHasFocus && FocusedPanel() is null ? CommandArea.Console : CommandArea.Panel,
                _ => CommandArea.OtherPage,
            },
            PanelsVisible = AnyPanelVisible,
            ActiveIsLeft = panel == _left,
            ActiveIsPlugin = panel.IsPlugin,
            OtherIsPlugin = other.IsPlugin,
            Cursor = cursor,
            CursorName = cursor is CursorKind.File or CursorKind.Folder ? state.Current.Name : null,
            Marked = state.Selection.Count,
            CanGet = panel.Provider is IGetFiles,
            CanMove = panel.Provider is IGetFiles { CanMove: true },
            OtherCanPut = other.Provider is IPutFiles,
            CanDelete = panel.Provider is IDeleteEntries,
            CanMakeFolder = panel.Provider is IMakeFolder,
            PanelHasCommands = panel.Provider is IPanelCommands,
            ConsoleTabs = _tabs.Count,
            DeleteToRecycleBin = _settings.DeleteToRecycleBin,
        };
    }

    /// <summary>The app's commands and what the plugins add; built each time, so a plugin's reload needs nothing more.</summary>
    private List<AppCommand> AllCommands()
    {
        var commands = new List<AppCommand>(CommandCatalog.All.Select(c => c with { Run = RunFor(c) }));
        foreach (var plugin in _plugins.Loaded)
        {
            foreach (var command in PluginManager.Guard(plugin, () => plugin.Instance!.Commands.ToList(), []))
            {
                commands.Add(new AppCommand($"plugin:{plugin.Id}:{command.Title}", plugin.Name, command.Title, VisiblePluginKey(command.Key), _ => true)
                {
                    Run = () => Fire(RunPluginGlobalCommandAsync(plugin, command)),
                });
            }

            foreach (var item in PluginManager.Guard(plugin, () => plugin.Instance!.MenuItems.ToList(), []))
            {
                commands.Add(new AppCommand($"plugin:{plugin.Id}:open:{item.Title}", plugin.Name, $"Open: {item.Title}", null, c => c.Area is CommandArea.Panel or CommandArea.Console)
                {
                    Run = () => Fire(OpenPluginAsync(_active, plugin, item.Open)),
                });
            }
        }

        var panel = _active;
        if (panel.Provider is IPanelCommands panelCommands && panel.Plugin is { } owner)
        {
            var current = panel.EntryOf(panel.State.Current);
            var selected = panel.TargetPluginEntries();
            foreach (var command in PluginManager.Guard(owner, () => panelCommands.Commands, []))
            {
                if (PluginManager.Guard(owner, () => command.AppliesTo?.Invoke(current, selected) ?? true, true))
                {
                    commands.Add(new AppCommand($"plugin:{owner.Id}:panel:{command.Title}", owner.Name, command.Title, command.Key, c => c.Area is CommandArea.Panel or CommandArea.Console)
                    {
                        Run = () => Fire(RunPluginCommandAsync(panel, command)),
                    });
                }
            }
        }

        return commands;
    }

    /// <summary>The host's keys win, so a plugin's global key that is one of them does nothing and is not shown.</summary>
    private static string? VisiblePluginKey(string? key) => key is not null && !CommandCatalog.HostKeys.Contains(key) ? key : null;

    private async Task RunPluginGlobalCommandAsync(LoadedPlugin plugin, PluginCommand command)
    {
        using var busy = _plugins.BeginOperation(plugin);
        try
        {
            using var wait = new WaitCard(Toasts, command.Title);
            await command.Run(wait.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancelled from the card.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            OperationDialogs.Message(this, plugin.Name, PluginManager.Describe(plugin, ex));
        }
    }

    /// <summary>A global command of a plugin by its key. A plugin panel's own command with that key goes first (FindCommand).</summary>
    private bool RunPluginGlobalKey(Key key, KeyModifiers modifiers)
    {
        foreach (var plugin in _plugins.Loaded)
        {
            foreach (var command in PluginManager.Guard(plugin, () => plugin.Instance!.Commands.ToList(), []))
            {
                if (VisiblePluginKey(command.Key) is { } text && KeyNames.TryParse(text, out var k, out var m) && k == key && m == modifiers)
                {
                    Fire(RunPluginGlobalCommandAsync(plugin, command));
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>What a catalog command does. By default what its key does, so the two cannot differ.</summary>
    private Action RunFor(AppCommand command) => command.Id switch
    {
        "panels.toggle" => TogglePanels,
        "window.maximize" => _titleBar.ToggleMaximize,
        "window.zoomIn" => () => Widgets.UiZoom.Step(1),
        "window.zoomOut" => () => Widgets.UiZoom.Step(-1),
        "window.zoomReset" => () => Widgets.UiZoom.Reset(),
        "window.help" => ShowHelp,
        "window.menu" => () => Fire(ShowTopMenuAsync()),
        "window.exit" => Close,
        "go.driveLeft" => () => Fire(ShowDriveMenuAsync(_left)),
        "go.driveRight" => () => Fire(ShowDriveMenuAsync(_right)),
        "tabs.new" => NewTab,
        "tabs.next" => () => NextTab(1),
        "tabs.previous" => () => NextTab(-1),
        "tabs.close" => () => CloseCurrentTab(),
        "panel.compare" => CompareFolders,
        "console.copy" => () => CopyTerminalSelection(),
        "console.paste" => PasteIntoTerminal,
        "plugins.menu" => () => Fire(ShowPluginMenuAsync()),
        "plugins.configure" => OpenPluginsPage,
        "files.find" => FindFiles,
        "plugins.panelCommands" => () => Fire(ShowPluginCommandsAsync(_active)),
        "settings.open" => OpenSettingsPage,
        "settings.file" => OpenSettingsFile,
        "settings.theme" => () => Fire(PickThemeAsync()),
        "settings.iconTheme" => () => Fire(PickIconThemeAsync()),
        "app.feedback" => ShowFeedback,
        "app.performance" => () => Fire(SendPerformanceReportAsync()),
        "app.updates" => () => Fire(CheckForUpdatesAsync()),
        "app.about" => ShowAbout,
        _ when command.Section is CommandCatalog.Viewer or CommandCatalog.Editor || command.Id == "window.close" => () => PressKey(command.Keys!),
        _ => () => RunOnPanel(_active, command),
    };

    private void CloseCurrentTab()
    {
        if (_tab is { } tab)
        {
            CloseTab(tab);
        }
    }

    private void RunOnPanel(FilePanel panel, AppCommand command)
    {
        if (command.Keys is { } keys && KeyNames.TryParse(keys, out var key, out var modifiers))
        {
            PanelKey(panel, key, modifiers);
        }
    }

    /// <summary>A viewer or editor page takes its keys from the real keyboard, so a command there presses its key.</summary>
    private static void PressKey(string keys)
    {
        if (!KeyNames.TryParse(keys, out var key, out var modifiers))
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => Platform.Desktop.Current.PressKey(key, modifiers), Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>A menu row: its title and key from the catalog.</summary>
    private MenuCommand MenuRow(string id, Action? run = null, bool isChecked = false)
    {
        var command = CommandCatalog.Get(id);
        return new MenuCommand(command.TitleIn(CaptureCommandContext() with { Marked = 0, CursorName = null }), command.Keys, run ?? RunFor(command), isChecked);
    }

    /// <summary>A menu row on a given panel, as the Left and Right menus have.</summary>
    private MenuCommand PanelRow(FilePanel panel, string id, bool isChecked = false)
    {
        var command = CommandCatalog.Get(id);
        return MenuRow(id, () => RunOnPanel(panel, command), isChecked);
    }

    /// <summary>Ctrl+Shift+P or a click on the box: type to find a command, or paste a path or an address.</summary>
    private async Task ShowCommandLineAsync()
    {
        if (_commandLineOpen || _titleBar.CommandBox.ActualWidth <= 0)
        {
            return;
        }

        _commandLineOpen = true;
        var context = CaptureCommandContext();
        var commands = AllCommands();
        var sectionsFirst = CommandCatalog.SectionsFirst(context);
        var previous = Keyboard.FocusedElement;
        var fromPage = IsPageOpen;
        var hadKeys = FocusedPanel();

        IReadOnlyList<QuickPickItem> ItemsFor(string text)
        {
            var clean = TypedLocation.Clean(text);
            if (clean.Length > 0 && IsPastedLocation(clean))
            {
                return [new QuickPickItem { Label = $"Open {clean}", Description = "Location", Tag = clean }];
            }

            return [.. CommandSearch.Rank(text, commands, context, _recentCommands, sectionsFirst).Select(r => new QuickPickItem
            {
                Label = r.Title,
                Description = r.Command.Section,
                Detail = Core.Commands.KeyLabel.Show(r.Command.Keys),
                Tag = r.Command,
            })];
        }

        QuickPickResult? result;
        try
        {
            result = await QuickPick.ShowAsync(_titleBar.CommandBox, [], new QuickPickOptions
            {
                Placeholder = Widgets.TitleBar.CommandBoxText,
                ItemsFor = ItemsFor,
                OverTarget = true,
            });
        }
        finally
        {
            _commandLineOpen = false;
        }

        if (fromPage)
        {
            (previous as Avalonia.Controls.Control)?.Focus();
        }
        else if (hadKeys is not null && result?.Item is null)
        {
            hadKeys.Focus();
        }

        switch (result?.Item?.Tag)
        {
            case AppCommand command:
                RememberCommand(command.Id);
                command.Run?.Invoke();
                break;
            case string location:
                await OnDriveChosenAsync(_active, new QuickPickResult(null, location), _active, fromTerminal: false);
                break;
        }
    }

    private void RememberCommand(string id)
    {
        _recentCommands.Remove(id);
        _recentCommands.Insert(0, id);
        if (_recentCommands.Count > RecentCommandCapacity)
        {
            _recentCommands.RemoveAt(_recentCommands.Count - 1);
        }
    }

    Task<IReadOnlyList<PluginApiCommand>> IPluginApiHandler.ListCommandsAsync() => OnUiAsync<IReadOnlyList<PluginApiCommand>>(() =>
    {
        var context = CaptureCommandContext();
        return Task.FromResult<IReadOnlyList<PluginApiCommand>>(
        [
            .. CommandSearch.Rank(string.Empty, AllCommands(), context, _recentCommands, CommandCatalog.SectionsFirst(context))
                .Select(r => new PluginApiCommand(r.Command.Id, r.Title, r.Command.Section, r.Command.Keys)),
        ]);
    });
}
