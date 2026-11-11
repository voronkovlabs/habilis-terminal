using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using HabilisTerminal.App.Plugins;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Plugins;

namespace HabilisTerminal.App;

/// <summary>The plugin manager (issue #166): install from a zip or a folder, turn off and on, remove. All without a restart.</summary>
public partial class MainWindow
{
    private PluginStore? _pluginStore;

    private PluginStore PluginStore => _pluginStore ??= PluginManager.StoreFor(App.Paths);

    private static string PluginInstallRoot => Path.Combine(Path.GetTempPath(), Core.Settings.AppPaths.ProductFolder, "plugin-install");

    private void OpenPluginsPage()
    {
        var page = new PluginsPage(PluginRows);
        page.CloseRequested += () => ClosePage();
        page.InstallRequested += () => RunOnPage(page, InstallPluginAsync);
        page.ToggleRequested += row => RunOnPage(page, () => TogglePluginAsync(row));
        page.RemoveRequested += row => RunOnPage(page, () => RemovePluginAsync(row));
        page.ReloadRequested += row => RunOnPage(page, () => ReloadPluginAsync(row));
        page.ConfigureRequested += row =>
        {
            // A plugin's settings may use the panels' menus, so they come back first.
            ClosePage();
            Fire(ConfigurePluginAsync(row));
        };
        ShowPage(page);
    }

    /// <summary>What is on disk and what runs, one row for each id.</summary>
    private IReadOnlyList<PluginRow> PluginRows()
    {
        var disk = PluginStore.List();
        var rows = disk.Select(p =>
        {
            var running = _plugins.Find(p.Id);
            var disabled = PluginStore.IsDisabled(p.Id);
            return new PluginRow(
                p.Id,
                running?.Name ?? p.Name,
                running?.Manifest?.Version ?? p.Version,
                p.Origin == PluginOrigin.Shipped ? PluginRowOrigin.Shipped : PluginRowOrigin.User,
                StateOf(running, disabled),
                running?.Error ?? p.Error,
                p.Folder,
                running);
        });

        // Loaded through the API from a build folder: runs until unloaded, not installed.
        var development = _plugins.Plugins.Where(p => !disk.Any(d => string.Equals(d.Id, p.Id, StringComparison.OrdinalIgnoreCase))).Select(p => new PluginRow(
            p.Id, p.Name, p.Manifest?.Version, PluginRowOrigin.Development, StateOf(p, false), p.Error, p.Source, p));
        return [.. rows.Concat(development).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static PluginRowState StateOf(LoadedPlugin? running, bool disabled) => running switch
    {
        { Instance: not null } => PluginRowState.On,
        not null => PluginRowState.Failed,
        _ => disabled ? PluginRowState.Off : PluginRowState.NotLoaded,
    };

    /// <summary>One action at a time; its outcome goes to the page's message line. The app's own check for an operation under way ends up here too.</summary>
    private static async void RunOnPage(PluginsPage page, Func<Task<string?>> action)
    {
        if (!page.TryBegin())
        {
            return;
        }

        string? message;
        try
        {
            message = await action();
        }
        catch (Exception ex) when (ex is PluginApiException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            message = ex.Message;
        }

        page.End(message);
    }

    private async Task<string?> InstallPluginAsync()
    {
        if (AskPluginSource() is not { } source)
        {
            return null;
        }

        using var package = await Task.Run(() => PluginStore.Open(source, PluginInstallRoot));
        if (PluginStore.Refusal(package) is { } refusal)
        {
            return refusal;
        }

        var manifest = package.Manifest;
        var previous = PluginStore.List().FirstOrDefault(p => string.Equals(p.Id, manifest.Id, StringComparison.OrdinalIgnoreCase));
        if (!ConfirmPluginInstall(manifest, previous))
        {
            return null;
        }

        var target = await Task.Run(() => PluginStore.Install(package));
        PluginApiOutcome outcome;
        try
        {
            outcome = await InstallAsync(target, null);
        }
        catch (PluginApiException)
        {
            // The running copy, if any, goes on as it was, so the new files must not take over at the next start.
            _plugins.RemoveFailed(target);
            PluginStore.Discard(target);
            throw;
        }

        if (outcome.Error is not null)
        {
            _plugins.RemoveFailed(target);
            PluginStore.Discard(target);
            return $"{manifest.Name} did not start: {outcome.Error}";
        }

        if (previous is { Origin: PluginOrigin.User } && !string.Equals(previous.Folder, target, StringComparison.OrdinalIgnoreCase))
        {
            PluginStore.Remove(previous.Folder);
        }

        PluginStore.SetDisabled(manifest.Id, false);
        return $"{manifest.Name} {manifest.Version} installed.";
    }

    /// <summary>The path to install from; a zip or a plugin folder under the panel's cursor is offered.</summary>
    private string? AskPluginSource()
    {
        var box = new TextBox { Text = PluginUnderCursor() ?? string.Empty };
        box.Loaded += (_, _) => box.SelectAll();
        var body = new StackPanel();
        body.Children.Add(box);
        var hint = new TextBlock { Text = "A .zip file or a folder with a plugin.json.", Margin = new Avalonia.Thickness(0, 6, 0, 0) };
        hint.Classes.Add("description");
        body.Children.Add(hint);
        var accepted = ThemedDialog.Show(this, "Install plugin from", body,
            [new DialogButton<bool>("Next", true, IsDefault: true), new DialogButton<bool>("Cancel", false, IsCancel: true)], false, box);
        var text = Core.Panels.TypedLocation.Clean(box.Text);
        return accepted && text.Length > 0 ? Path.GetFullPath(Core.Panels.TypedLocation.ForGoto(text, _active.LocalPath, _ => false)) : null;
    }

    private string? PluginUnderCursor()
    {
        var panel = _active;
        if (panel.IsPlugin || panel.State.Count == 0 || panel.State.Current.IsParent)
        {
            return null;
        }

        var path = Path.Combine(panel.CurrentPath, panel.State.Current.Name);
        return path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(path, PluginManifest.FileName)) ? path : null;
    }

    private bool ConfirmPluginInstall(PluginManifest manifest, InstalledPlugin? previous)
    {
        var body = new StackPanel();
        var what = new TextBlock { Text = previous?.Version is { } version ? $"{manifest.Name} {manifest.Version}, in place of version {version}." : $"{manifest.Name} {manifest.Version} ({manifest.Id})." };
        what.Classes.Add("label");
        body.Children.Add(what);
        var warning = new TextBlock { Text = "A plugin runs with all the rights of this app and can read and change your files. Install only what you trust.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 460, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
        warning.Classes.Add("description");
        body.Children.Add(warning);
        return ThemedDialog.Show(this, "Install plugin", body,
            [new DialogButton<bool>("_Install", true, IsDefault: true), new DialogButton<bool>("Cancel", false, IsCancel: true)], false);
    }

    private async Task<string?> TogglePluginAsync(PluginRow row)
    {
        if (row.State == PluginRowState.On || row.State == PluginRowState.Failed)
        {
            await UninstallAsync(row.Id);
            if (row.Origin == PluginRowOrigin.Development)
            {
                return $"{row.Name} unloaded. It was not installed, so it is gone from this list.";
            }

            PluginStore.SetDisabled(row.Id, true);
            return $"{row.Name} is off.";
        }

        PluginStore.SetDisabled(row.Id, false);
        var outcome = await InstallAsync(row.Folder, null);
        return outcome.Error ?? $"{row.Name} is on.";
    }

    private async Task<string?> RemovePluginAsync(PluginRow row)
    {
        switch (row.Origin)
        {
            case PluginRowOrigin.Shipped:
                return $"{row.Name} comes with the app and can only be turned off (Space).";
            case PluginRowOrigin.Development:
                return $"{row.Name} is not installed. Space unloads it.";
        }

        if (!OperationDialogs.ConfirmDelete(this, [row.Name], permanent: true))
        {
            return null;
        }

        if (row.Running is not null)
        {
            await UninstallAsync(row.Id);
        }

        var deleted = PluginStore.Remove(row.Folder);
        PluginStore.SetDisabled(row.Id, false);

        // A copy that comes with the app was hidden by this one: it is back.
        if (PluginStore.List().FirstOrDefault(p => string.Equals(p.Id, row.Id, StringComparison.OrdinalIgnoreCase)) is { } shipped)
        {
            await InstallAsync(shipped.Folder, null);
            return $"{row.Name} removed. Version {shipped.Version}, which comes with the app, is back.";
        }

        return deleted ? $"{row.Name} removed." : $"{row.Name} removed. Its files go at the next start.";
    }

    private async Task<string?> ReloadPluginAsync(PluginRow row)
    {
        if (row.State == PluginRowState.Off)
        {
            return $"{row.Name} is off. Space turns it on.";
        }

        var outcome = await InstallAsync(row.Running?.Source ?? row.Folder, null);
        return outcome.Error ?? $"{row.Name} reloaded.";
    }

    private async Task ConfigurePluginAsync(PluginRow row)
    {
        if (row.Running is not { Instance: { } instance } plugin)
        {
            return;
        }

        using var busy = _plugins.BeginOperation(plugin);
        try
        {
            await instance.ConfigureAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            OperationDialogs.Message(this, plugin.Name, PluginManager.Describe(plugin, ex));
        }
    }
}
