using System.IO;
using HabilisTerminal.App.Panels;
using HabilisTerminal.Core.Plugins;

namespace HabilisTerminal.App;

/// <summary>
/// Loading and unloading plugins while the app runs (issue #150, docs/plugin-api.md). A new version is loaded and checked
/// first, so a plugin that does not build or load leaves the running one as it was.
/// </summary>
public partial class MainWindow : IPluginApiHandler
{
    private static readonly TimeSpan ShutdownLimit = TimeSpan.FromSeconds(5);

    private PluginApiServer? _pluginApi;

    private void StartPluginApi()
    {
        PluginLoader.CleanShadowCopies(PluginShadowRoot);
        var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0);
        _pluginApi = new PluginApiServer(PluginApiServer.PipeNameFor(Environment.ProcessId), this, version);
        _pluginApi.Start();
        Closed += (_, _) => _pluginApi?.Dispose();
    }

    private static string PluginShadowRoot => Path.Combine(Path.GetTempPath(), Core.Settings.AppPaths.ProductFolder, "plugin-shadow");

    private Task<T> OnUiAsync<T>(Func<Task<T>> work) => Dispatcher.InvokeAsync(work).Task.Unwrap();

    Task<IReadOnlyList<PluginApiEntry>> IPluginApiHandler.ListAsync() =>
        OnUiAsync<IReadOnlyList<PluginApiEntry>>(() => Task.FromResult<IReadOnlyList<PluginApiEntry>>(_plugins.Plugins.Select(p => new PluginApiEntry(
            p.Id, p.Name, p.Manifest?.Version, p.Manifest?.Sdk, p.Folder, p.Source, p.Instance is null ? "failed" : "loaded", p.Error, p.ErrorDetail, p.Context?.IsCollectible == true)).ToList()));

    Task<PluginApiOutcome> IPluginApiHandler.LoadAsync(string folder) => OnUiAsync(() => InstallAsync(Path.GetFullPath(folder), null));

    Task<PluginApiOutcome> IPluginApiHandler.ReloadAsync(string id) => OnUiAsync(() =>
        _plugins.Find(id) is { } plugin
            ? InstallAsync(plugin.Source, id)
            : throw new PluginApiException(PluginApiException.NotFound, $"No plugin \"{id}\" is loaded."));

    Task<PluginApiOutcome> IPluginApiHandler.UnloadAsync(string id) => OnUiAsync(() => UninstallAsync(id));

    /// <summary>Loads a plugin from a temporary copy, so its build output stays free to overwrite, in place of any running copy.</summary>
    private async Task<PluginApiOutcome> InstallAsync(string source, string? expectedId)
    {
        var fresh = await Task.Run(() => LoadCopy(source));
        if (fresh.Manifest is null || fresh.Instance is null)
        {
            var code = fresh.Error?.Contains("needs SDK", StringComparison.Ordinal) == true ? PluginApiException.IncompatibleSdk : PluginApiException.LoadFailed;
            if (_plugins.Find(fresh.Id) is null)
            {
                // Nothing runs under that id: the list keeps the reason for the developer.
                _plugins.Add(fresh);
            }

            throw new PluginApiException(code, fresh.Error ?? $"{source} could not be loaded.");
        }

        if (expectedId is not null && !string.Equals(expectedId, fresh.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginApiException(PluginApiException.LoadFailed, $"{source} now holds the plugin \"{fresh.Id}\", not \"{expectedId}\".");
        }

        ThrowIfBusy(fresh.Id);
        var (closed, freed) = await RemoveRunningAsync(fresh.Id);
        var ready = InitializePlugin(fresh);
        _plugins.Add(ready);
        UpdateShellPluginLists();
        if (ready.Instance is null)
        {
            return new PluginApiOutcome(ready.Id, "failed", freed, closed.Count, 0, ready.Error);
        }

        var reopened = 0;
        foreach (var (panel, location) in closed)
        {
            if (CanReopen(location) && await NavigateOrOpenAsync(panel, location))
            {
                reopened++;
            }
        }

        return new PluginApiOutcome(ready.Id, "loaded", freed, closed.Count, reopened, null);
    }

    private async Task<PluginApiOutcome> UninstallAsync(string id)
    {
        if (_plugins.Find(id) is null)
        {
            throw new PluginApiException(PluginApiException.NotFound, $"No plugin \"{id}\" is loaded.");
        }

        ThrowIfBusy(id);
        var (closed, freed) = await RemoveRunningAsync(id);
        UpdateShellPluginLists();
        return new PluginApiOutcome(id, "unloaded", freed, closed.Count, 0, null);
    }

    private void ThrowIfBusy(string id)
    {
        if (_plugins.IsBusy(id))
        {
            throw new PluginApiException(PluginApiException.Busy, $"The plugin \"{id}\" has an operation under way. Finish or cancel it, then try again.");
        }
    }

    private static LoadedPlugin LoadCopy(string source)
    {
        if (!File.Exists(Path.Combine(source, PluginManifest.FileName)))
        {
            return new LoadedPlugin(source, null, null, $"{source} has no {PluginManifest.FileName}.") { Source = source };
        }

        var copy = PluginLoader.ShadowCopy(source, PluginShadowRoot);
        return PluginLoader.Load(copy, source);
    }

    /// <summary>Closes a plugin's panels in every tab, shuts it down and lets go of it. Freed: its code really left memory; null when nothing ran.</summary>
    private async Task<(List<(FilePanel Panel, string Location)> Closed, bool? Freed)> RemoveRunningAsync(string id)
    {
        var closed = new List<(FilePanel, string)>();
        var running = _plugins.Find(id);
        if (running is null)
        {
            return (closed, null);
        }

        foreach (var panel in AllPanels().Where(p => string.Equals(p.Plugin?.Id, id, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            closed.Add((panel, panel.Provider!.Location));
            await panel.LeavePluginAsync();
            await panel.PluginClosing;
        }

        if (running.Instance is { } instance)
        {
            try
            {
                await instance.ShutdownAsync().WaitAsync(ShutdownLimit);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Trace($"plugin {id} shutdown failed: {ex.Message}");
            }
        }

        _plugins.Remove(running);
        var context = running.Context;
        running = null;
        return (closed, context is null ? null : await UnloadAsync(context));
    }

    /// <summary>Whether the code really left memory: a static or a running thread of the plugin keeps it alive until the app restarts.</summary>
    private static Task<bool> UnloadAsync(System.Runtime.Loader.AssemblyLoadContext context)
    {
        if (!context.IsCollectible)
        {
            return Task.FromResult(false);
        }

        var weak = PluginLoader.Unload(context);
        return Task.Run(async () =>
        {
            for (var i = 0; i < 20 && weak.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(100);
            }

            return !weak.IsAlive;
        });
    }

    /// <summary>A location the plugins can open again: an address they registered, or a place inside a file one of them takes.</summary>
    private bool CanReopen(string location) => Path.IsPathFullyQualified(location) || _plugins.ForAddress(location) is not null;

    /// <summary>The consoles ask the channel's files which addresses and commands to catch; they change with the plugins.</summary>
    private void UpdateShellPluginLists()
    {
        var prefixes = _plugins.Prefixes();
        var commands = _plugins.AddressCommands();
        foreach (var tab in _tabs)
        {
            tab.Host.AddressPrefixes = prefixes;
            tab.Host.AddressCommands = commands;
        }
    }
}
