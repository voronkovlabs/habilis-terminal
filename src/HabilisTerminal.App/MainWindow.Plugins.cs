using System.IO;
using System.Windows.Input;
using HabilisTerminal.App.Input;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Plugins;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Operations;
using HabilisTerminal.Core.Panels;
using HabilisTerminal.Core.Plugins;
using HabilisTerminal.Plugins;

namespace HabilisTerminal.App;

/// <summary>
/// Plugins in the window (docs/plugins.md §5, issue #49): loading, F11, the drive menu and command-line prefixes,
/// and the panel keys of plugin panels (F3, F5 / F6 both ways, F7, F8, plugin commands).
/// </summary>
public partial class MainWindow : IPluginWindow
{
    private readonly PluginManager _plugins = new();

    private void LoadPlugins()
    {
        _plugins.LoadAll(App.Paths, HostFor);
        foreach (var failed in _plugins.Plugins.Where(p => p.Error is not null))
        {
            Trace($"plugin {failed.Id} not loaded: {failed.Error}");
        }

        StartPluginApi();
    }

    private PluginHostServices HostFor(LoadedPlugin plugin) => new(
        plugin,
        new PluginDialogs(this, () => _active),
        this,
        App.Paths.PluginSettingsDirectory,
        Path.Combine(Path.GetTempPath(), Core.Settings.AppPaths.ProductFolder, "plugins"));

    private LoadedPlugin InitializePlugin(LoadedPlugin plugin) => PluginManager.Initialize(plugin, HostFor);

    /// <summary>Opens a folder, or a plugin address ("s3://...", "sqs:...") from history or the drive menu.</summary>
    private async Task<bool> NavigateOrOpenAsync(FilePanel panel, string target)
    {
        if (!Path.IsPathFullyQualified(target) && _plugins.ForAddress(target) is { } plugin)
        {
            return await OpenPluginAsync(panel, plugin, token => plugin.Instance!.OpenAddressAsync(target, token));
        }

        if (Path.IsPathFullyQualified(target) && !Directory.Exists(target) && await OpenLocationInFileAsync(panel, target))
        {
            return true;
        }

        return await panel.NavigateAsync(target);
    }

    /// <summary>Runs a plugin entry point and shows the panel it returns; errors are shown, never thrown.</summary>
    private async Task<bool> OpenPluginAsync(FilePanel panel, LoadedPlugin plugin, Func<CancellationToken, Task<IPanelProvider?>> open, string? returnPath = null, string? returnFocus = null)
    {
        var (provider, _) = await AskPluginAsync(panel, plugin, open);
        return provider is not null && await ShowPluginAsync(panel, plugin, provider, returnPath, returnFocus);
    }

    /// <summary>The panel a plugin returns; Failed when it could not say, which is shown and is not the same as "not mine".</summary>
    private async Task<(IPanelProvider? Provider, bool Failed)> AskPluginAsync(FilePanel panel, LoadedPlugin plugin, Func<CancellationToken, Task<IPanelProvider?>> open)
    {
        using var busy = _plugins.BeginOperation(plugin);
        try
        {
            using var wait = new WaitCard(Toasts, $"Opening {plugin.Name}");
            return (await open(wait.Token), false);
        }
        catch (OperationCanceledException)
        {
            return (null, true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            panel.Message = PluginManager.Describe(plugin, ex);
            return (null, true);
        }
    }

    private async Task<bool> ShowPluginAsync(FilePanel panel, LoadedPlugin plugin, IPanelProvider provider, string? returnPath, string? returnFocus)
    {
        Activate(panel);
        FocusActivePanel();
        return await panel.OpenPluginAsync(plugin, provider, returnPath, returnFocus);
    }

    /// <summary>
    /// Enter on a file a plugin claims, or Ctrl+PgDn on any file: going inside it (issues #51, #148). A file no plugin
    /// takes after Enter does what Enter does with any file.
    /// </summary>
    private async Task OpenFileAsFolderAsync(FilePanel panel, PanelItem item, bool anyFormat)
    {
        var (opened, failed) = await OpenFileInPluginAsync(panel, Path.Combine(panel.CurrentPath, item.Name), string.Empty, anyFormat);
        if (opened || failed)
        {
            return;
        }

        if (anyFormat)
        {
            panel.Message = $"No plugin can open '{item.Name}'.";
        }
        else
        {
            OnPanelExecuteRequested(panel, item);
        }
    }

    /// <summary>Folder history or a pasted path: a place inside a file (<c>C:\data\a.zip\docs</c>).</summary>
    private async Task<bool> OpenLocationInFileAsync(FilePanel panel, string location)
    {
        var split = await Task.Run(() => PluginFiles.SplitLocation(location, File.Exists));
        return split is { } place && (await OpenFileInPluginAsync(panel, place.File, place.InnerPath, anyFormat: true)).Opened;
    }

    /// <summary>Asks the plugins in turn, off the UI thread; the first panel opens and ".." at its top returns to the file.</summary>
    private async Task<(bool Opened, bool Failed)> OpenFileInPluginAsync(FilePanel panel, string file, string innerPath, bool anyFormat)
    {
        var name = Path.GetFileName(file);
        var request = new FileOpenRequest(file, innerPath, anyFormat);
        foreach (var plugin in _plugins.ForFile(name, anyFormat))
        {
            var (provider, failed) = await AskPluginAsync(panel, plugin, token => Task.Run(() => plugin.Instance!.OpenFileAsync(request, token), token));
            if (failed)
            {
                return (false, true);
            }

            if (provider is not null)
            {
                return (await ShowPluginAsync(panel, plugin, provider, Path.GetDirectoryName(file), name), false);
            }
        }

        return (false, false);
    }

    /// <summary>The command line's Enter on "prefix:..." or "cd prefix:..." (integration script): open the plugin in the active panel.</summary>
    private void OnPluginAddress(string address)
    {
        if (_plugins.ForAddress(address) is not { } plugin)
        {
            return;
        }

        if (!AnyPanelVisible)
        {
            SetPanelVisibility(_active == _left, _active == _right);
        }

        Fire(OpenPluginAsync(_active, plugin, token => plugin.Instance!.OpenAddressAsync(address.Trim(), token)));
    }

    /// <summary>F11: the plugins' menu items.</summary>
    private async Task ShowPluginMenuAsync()
    {
        var panel = _active;
        var entries = _plugins.MenuItems();
        if (entries.Count == 0)
        {
            panel.Message = _plugins.Plugins.Count == 0
                ? $"No plugins are installed. Put plugin folders into {App.Paths.PluginsDirectory}."
                : "No plugin has menu items.";
            return;
        }

        var items = entries.Select(e => new QuickPickItem { Label = e.Item.Title, Description = e.Plugin.Name, Tag = e, Icon = Theming.Codicon.Folder }).ToList();
        var result = await QuickPick.ShowAsync(panel, items, new QuickPickOptions { Title = "Plugins", Placeholder = "Filter" });
        panel.Focus(); // Esc keeps the panel focused, as the drive menu does
        if (result?.Item?.Tag is ValueTuple<LoadedPlugin, PluginMenuItem> chosen)
        {
            await OpenPluginAsync(panel, chosen.Item1, chosen.Item2.Open);
        }
    }

    /// <summary>F9 > Options > Plugins and Alt+Shift+F9: loaded and failed plugins; Enter configures one.</summary>
    private async Task ShowPluginsConfigurationAsync()
    {
        var panel = _active;
        var items = _plugins.Plugins.Select(p => new QuickPickItem
        {
            Label = p.Name,
            Description = p.Error ?? (p.Instance is { } plugin && PluginManager.Guard(p, () => plugin.HasConfiguration, false) ? $"{p.Manifest!.Version} · Enter to configure" : $"{p.Manifest!.Version}"),
            Tag = p,
        }).ToList();
        if (items.Count == 0)
        {
            panel.Message = $"No plugins are installed. Put plugin folders into {App.Paths.PluginsDirectory}.";
            return;
        }

        var result = await QuickPick.ShowAsync(panel, items, new QuickPickOptions { Title = "Plugins", Placeholder = "Filter plugins · F5 reloads a plugin", CommandKeys = [Key.F5] });
        panel.Focus();
        if (result is { Command: Key.F5, Item.Tag: LoadedPlugin reloading })
        {
            try
            {
                var outcome = await InstallAsync(reloading.Source, reloading.Id);
                panel.Message = outcome.Error ?? $"{reloading.Name} reloaded.";
            }
            catch (PluginApiException ex)
            {
                panel.Message = ex.Message;
            }

            return;
        }

        if (result?.Item?.Tag is LoadedPlugin { Instance: { } instance } chosen && PluginManager.Guard(chosen, () => instance.HasConfiguration, false))
        {
            using var busy = _plugins.BeginOperation(chosen);
            try
            {
                await instance.ConfigureAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                OperationDialogs.Message(this, chosen.Name, PluginManager.Describe(chosen, ex));
            }
        }
    }

    /// <summary>Plugin keys: F11 everywhere; in a plugin panel its commands and the file keys; F5 / F6 into a plugin panel.</summary>
    private bool HandlePluginKey(FilePanel panel, Key key, KeyModifiers modifiers)
    {
        if (key == Key.F11 && modifiers == KeyModifiers.None)
        {
            Fire(ShowPluginMenuAsync());
            return true;
        }

        if (key == Key.F9 && modifiers == (KeyModifiers.Alt | KeyModifiers.Shift))
        {
            Fire(ShowPluginsConfigurationAsync());
            return true;
        }

        if (!(panel.IsPlugin && FindCommand(panel, key, modifiers) is not null) && RunPluginGlobalKey(key, modifiers))
        {
            return true;
        }

        if (panel.IsPlugin)
        {
            if (FindCommand(panel, key, modifiers) is { } command)
            {
                Fire(RunPluginCommandAsync(panel, command));
                return true;
            }

            switch (key)
            {
                case Key.F3 when modifiers == KeyModifiers.None:
                    if (panel.EntryOf(panel.State.Current) is { } entry)
                    {
                        Fire(PluginViewAsync(panel, entry));
                    }

                    return true;
                case Key.F5 when modifiers == KeyModifiers.None:
                case Key.F6 when modifiers == KeyModifiers.None:
                    Fire(PluginGetFilesAsync(panel, move: key == Key.F6));
                    return true;
                case Key.F7 when modifiers == KeyModifiers.None:
                    Fire(PluginMakeFolderAsync(panel));
                    return true;
                case Key.F8 when modifiers == KeyModifiers.None:
                case Key.Delete when modifiers is KeyModifiers.None or KeyModifiers.Shift:
                    Fire(PluginDeleteAsync(panel));
                    return true;
                case Key.F12 when modifiers == KeyModifiers.Control:
                    Fire(ShowPluginSortMenuAsync(panel));
                    return true;
                case Key.F4 or Key.F5 or Key.F6 when modifiers is KeyModifiers.None or KeyModifiers.Shift:
                case Key.A when modifiers == KeyModifiers.Control:
                    panel.Message = $"Not supported by {panel.Plugin!.Name}.";
                    return true;
            }

            return false;
        }

        if (Other(panel).IsPlugin && key is Key.F5 or Key.F6 && modifiers == KeyModifiers.None)
        {
            Fire(PluginPutFilesAsync(panel, Other(panel), move: key == Key.F6));
            return true;
        }

        return false;
    }

    private static PanelCommand? FindCommand(FilePanel panel, Key key, KeyModifiers modifiers)
    {
        if (panel.Provider is not IPanelCommands commands)
        {
            return null;
        }

        var list = PluginManager.Guard(panel.Plugin!, () => commands.Commands, []);
        return list.FirstOrDefault(c => c.Key is { } text && KeyNames.TryParse(text, out var k, out var m) && k == key && m == modifiers);
    }

    /// <summary>F9 > Commands > Plugin commands: every command of the plugin panel, with its key.</summary>
    private async Task ShowPluginCommandsAsync(FilePanel panel)
    {
        if (panel.Provider is not IPanelCommands commands)
        {
            panel.Message = panel.IsPlugin ? $"{panel.Plugin!.Name} has no commands." : "The panel is not a plugin panel.";
            return;
        }

        var items = PluginManager.Guard(panel.Plugin!, () => commands.Commands, [])
            .Select(c => new QuickPickItem { Label = c.Title, Detail = c.Key ?? string.Empty, Tag = c }).ToList();
        var result = await QuickPick.ShowAsync(panel, items, new QuickPickOptions { Title = $"{panel.Plugin!.Name} commands", Placeholder = "Filter commands" });
        panel.Focus();
        if (result?.Item?.Tag is PanelCommand command)
        {
            await RunPluginCommandAsync(panel, command);
        }
    }

    private async Task RunPluginCommandAsync(FilePanel panel, PanelCommand command)
    {
        var plugin = panel.Plugin!;
        using var busy = _plugins.BeginOperation(plugin);
        using var control = new OperationControl();
        var progress = new PluginProgress(Dispatcher, () => Toasts.Add(command.Title, control));
        progress.ShowAfter(TimeSpan.FromMilliseconds(400));
        var current = panel.EntryOf(panel.State.Current);
        try
        {
            await command.Run(new PanelCommandContext(current, panel.TargetPluginEntries(), progress, control.Token));
        }
        catch (OperationCanceledException)
        {
            // Cancelled from the card.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            OperationDialogs.Message(this, command.Title, PluginManager.Describe(plugin, ex));
        }
        finally
        {
            progress.Close();
        }

        if (panel.Plugin == plugin)
        {
            await panel.ListPluginAsync();
        }
    }

    /// <summary>F3 and Enter on a plugin entry: the plugin's own view, else a temporary copy in the viewer.</summary>
    private async Task PluginViewAsync(FilePanel panel, PanelEntry entry)
    {
        var plugin = panel.Plugin!;
        using var busy = _plugins.BeginOperation(plugin);
        var provider = panel.Provider!;
        try
        {
            if (provider is IViewEntry viewer)
            {
                // A view that takes longer than half a second gets a card with Cancel.
                using var control = new OperationControl();
                var progress = new PluginProgress(Dispatcher, () => Toasts.Add($"Opening {entry.Name}", control));
                var viewing = viewer.ViewAsync(entry, control.Token);
                _ = Task.Delay(500).ContinueWith(_ =>
                {
                    if (!viewing.IsCompleted)
                    {
                        progress.Begin(entry.Name);
                    }
                }, TaskScheduler.Default);
                (string Title, string Text)? view;
                try
                {
                    view = await viewing;
                }
                catch (OperationCanceledException) when (control.Token.IsCancellationRequested)
                {
                    return;
                }
                finally
                {
                    progress.Close();
                }

                if (view is { } shown)
                {
                    await ViewTextAsync(shown.Title, shown.Text);
                    return;
                }
            }

            if (provider is IGetFiles getter && !entry.IsFolder)
            {
                var folder = Path.Combine(Path.GetTempPath(), Core.Settings.AppPaths.ProductFolder, "plugins", plugin.Id, "view-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(folder);
                var outcome = await RunPluginOperationAsync(plugin, $"Opening {entry.Name}", (progress, token) =>
                    getter.GetFilesAsync(new TransferRequest([entry], folder, move: false, _ => Task.FromResult(OverwriteChoice.Overwrite)), progress, token));
                var file = Directory.EnumerateFiles(folder).FirstOrDefault();
                if (outcome is { Failed: 0 } && file is not null)
                {
                    OpenViewer(file, panel);
                }
                else
                {
                    Report(panel, outcome); // issue #157: nothing to view must say why
                }

                return;
            }

            panel.Message = $"Not supported by {plugin.Name}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            OperationDialogs.Message(this, plugin.Name, PluginManager.Describe(plugin, ex));
        }
    }

    /// <summary>F5 / F6 from a plugin panel: download into the other panel's folder.</summary>
    private async Task PluginGetFilesAsync(FilePanel source, bool move)
    {
        var plugin = source.Plugin!;
        using var busy = _plugins.BeginOperation(plugin);
        if (source.Provider is not IGetFiles getter || (move && !getter.CanMove))
        {
            source.Message = move ? $"Moving from {plugin.Name} is not supported." : $"Copying from {plugin.Name} is not supported.";
            return;
        }

        var target = Other(source);
        if (target.IsPlugin)
        {
            source.Message = "Copying between two plugin panels is not supported.";
            return;
        }

        var entries = source.TargetPluginEntries();
        if (entries.Count == 0)
        {
            return;
        }

        if (OperationDialogs.CopyOrMove(this, move, entries.Select(e => e.Name).ToList(), target.CurrentPath) is not { } choice)
        {
            return;
        }

        var destination = Path.GetFullPath(choice.Destination);
        Directory.CreateDirectory(destination);
        var ask = OverwriteAsker(choice.Options.ExistingFiles);
        var outcome = await RunPluginOperationAsync(plugin, move ? "Move" : "Copy", (progress, token) =>
            getter.GetFilesAsync(new TransferRequest(entries, destination, move, ask), progress, token));
        FilePanel.InvalidateFolderSizes(destination);
        source.State.Selection.Clear();
        await source.ListPluginAsync();
        await target.ReloadAsync();
        Report(source, outcome);
    }

    /// <summary>F5 / F6 from a folder into a plugin panel: upload; a move deletes the originals after a clean upload.</summary>
    private async Task PluginPutFilesAsync(FilePanel source, FilePanel target, bool move)
    {
        var plugin = target.Plugin!;
        using var busy = _plugins.BeginOperation(plugin);
        if (target.Provider is not IPutFiles putter)
        {
            source.Message = $"Copying into {plugin.Name} is not supported.";
            return;
        }

        var names = source.State.TargetEntries().Select(e => e.Name).ToList();
        if (names.Count == 0)
        {
            return;
        }

        if (OperationDialogs.CopyOrMove(this, move, names, target.CurrentPath) is not { } choice)
        {
            return;
        }

        var paths = names.Select(n => Path.Combine(source.CurrentPath, n)).ToList();
        var ask = OverwriteAsker(choice.Options.ExistingFiles);
        var outcome = await RunPluginOperationAsync(plugin, move ? "Move" : "Copy", (progress, token) => putter.PutFilesAsync(paths, ask, progress, token));
        if (move && outcome is { Failed: 0, Skipped: 0 })
        {
            await Task.Run(() =>
            {
                foreach (var path in paths)
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    else if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            });
            FilePanel.InvalidateFolderSizes(source.CurrentPath);
        }
        else if (move && outcome is not null)
        {
            source.Message = "Some items were not uploaded, so the local originals were kept.";
        }

        source.State.Selection.Clear();
        await source.ReloadAsync();
        await target.ListPluginAsync();
        Report(target, outcome);
    }

    private async Task PluginDeleteAsync(FilePanel panel)
    {
        var plugin = panel.Plugin!;
        using var busy = _plugins.BeginOperation(plugin);
        if (panel.Provider is not IDeleteEntries deleter)
        {
            panel.Message = $"Deleting is not supported by {plugin.Name}.";
            return;
        }

        var entries = panel.TargetPluginEntries();
        if (entries.Count == 0 || !OperationDialogs.ConfirmDelete(this, entries.Select(e => e.Name).ToList(), permanent: true))
        {
            return;
        }

        var outcome = await RunPluginOperationAsync(plugin, "Delete", (progress, token) => deleter.DeleteAsync(entries, progress, token));
        panel.State.Selection.Clear();
        await panel.ListPluginAsync();
        Report(panel, outcome);
    }

    private async Task PluginMakeFolderAsync(FilePanel panel)
    {
        var plugin = panel.Plugin!;
        using var busy = _plugins.BeginOperation(plugin);
        if (panel.Provider is not IMakeFolder maker)
        {
            panel.Message = $"Making folders is not supported by {plugin.Name}.";
            return;
        }

        if (OperationDialogs.MakeDirectory(this) is not { } input)
        {
            return;
        }

        var name = input.Text.Trim();
        try
        {
            await maker.MakeFolderAsync(name, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            OperationDialogs.Message(this, "Create folder", PluginManager.Describe(plugin, ex));
            return;
        }

        await panel.ListPluginAsync(name.TrimEnd('/', '\\'));
    }

    /// <summary>Ctrl+F12 in a plugin panel: name, size, date and the plugin's own sort keys.</summary>
    private static async Task ShowPluginSortMenuAsync(FilePanel panel)
    {
        var keys = PluginManager.Guard(panel.Plugin!, () => panel.Provider!.SortKeys, []);
        var items = new List<QuickPickItem>
        {
            new() { Label = "Name", Detail = "Ctrl+F3", Tag = Core.FileSystem.SortMode.Name },
            new() { Label = "Size", Detail = "Ctrl+F6", Tag = Core.FileSystem.SortMode.Size },
            new() { Label = "Modified", Detail = "Ctrl+F5", Tag = Core.FileSystem.SortMode.Modified },
        };
        items.AddRange(keys.Select(k => new QuickPickItem { Label = k.Title, Tag = k }));
        var result = await QuickPick.ShowAsync(panel, items, new QuickPickOptions { Title = "Sort by", Placeholder = "Filter sort keys" });
        panel.Focus();
        switch (result?.Item?.Tag)
        {
            case Core.FileSystem.SortMode mode:
                panel.State.ApplySortKey(mode);
                break;
            case PanelSortKey key:
                panel.State.ApplyCustomSortKey(key.Key);
                break;
            default:
                return;
        }

        panel.Refresh();
    }

    /// <summary>Runs a plugin operation with a progress card and Cancel; null when it failed (the message is shown).</summary>
    private async Task<OperationOutcome?> RunPluginOperationAsync(LoadedPlugin plugin, string title, Func<IOperationProgress, CancellationToken, Task<OperationOutcome>> work)
    {
        using var measure = Core.Diagnostics.Perf.MeasureAsync("plugin.operation", plugin.Id);
        using var control = new OperationControl();
        var card = Toasts.Add(title, control);
        var progress = new PluginProgress(Dispatcher, () => card);
        try
        {
            return await Task.Run(() => work(progress, control.Token), control.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            OperationDialogs.Message(this, title, PluginManager.Describe(plugin, ex));
            return null;
        }
        finally
        {
            progress.Close();
            card.Close();
        }
    }

    private static void Report(FilePanel panel, OperationOutcome? outcome)
    {
        if (outcome is null)
        {
            return;
        }

        panel.Message = outcome.Message ?? (outcome.Failed > 0
            ? $"{outcome.Failed} failed, {outcome.Succeeded} done{(outcome.Skipped > 0 ? $", {outcome.Skipped} skipped" : string.Empty)}."
            : null);
    }

    /// <summary>The copy dialog's "existing files" choice as the plugin's question about an existing target.</summary>
    private Func<string, Task<OverwriteChoice>> OverwriteAsker(ExistingFilePolicy policy) => policy switch
    {
        ExistingFilePolicy.Overwrite => _ => Task.FromResult(OverwriteChoice.OverwriteAll),
        ExistingFilePolicy.Skip => _ => Task.FromResult(OverwriteChoice.SkipAll),
        _ => name => Dispatcher.InvokeAsync(() => ThemedDialog.Show(this, "File exists", LabelBlock($"{name} already exists."),
            [
                new DialogButton<OverwriteChoice>("_Overwrite", OverwriteChoice.Overwrite, IsDefault: true),
                new DialogButton<OverwriteChoice>("_Skip", OverwriteChoice.Skip),
                new DialogButton<OverwriteChoice>("Overwrite _all", OverwriteChoice.OverwriteAll),
                new DialogButton<OverwriteChoice>("S_kip all", OverwriteChoice.SkipAll),
                new DialogButton<OverwriteChoice>("Cancel", OverwriteChoice.Cancel, IsCancel: true),
            ], OverwriteChoice.Cancel)).Task,
    };

    private static System.Windows.Controls.TextBlock LabelBlock(string text)
    {
        var block = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = System.Windows.TextWrapping.Wrap };
        block.SetResourceReference(StyleProperty, "Habi.LabelStyle");
        return block;
    }

    Task IPluginWindow.ViewFileAsync(string path, string? title) =>
        Dispatcher.InvokeAsync(() => OpenViewer(path, _active)).Task;

    /// <summary>Text from a plugin (a job summary, a full record) in the viewer, through a temporary file.</summary>
    public Task ViewTextAsync(string title, string text) =>
        Dispatcher.InvokeAsync(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), Core.Settings.AppPaths.ProductFolder, "plugin-views");
            Directory.CreateDirectory(folder);
            var name = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            name = name.Length == 0 ? "view" : name[..Math.Min(name.Length, 80)];
            var file = Path.Combine(folder, Path.HasExtension(name) ? name : name + ".txt");
            File.WriteAllText(file, text);
            OpenViewer(file, _active);
        }).Task;

    void IPluginWindow.Refresh(IPanelProvider provider) =>
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var panel in new[] { _left, _right }.Where(p => p.Provider == provider))
            {
                _ = panel.ListPluginAsync(quiet: true);
            }
        });

    void IPluginWindow.ShowStatus(string message) => Dispatcher.BeginInvoke(() => _active.Message = message);
}
