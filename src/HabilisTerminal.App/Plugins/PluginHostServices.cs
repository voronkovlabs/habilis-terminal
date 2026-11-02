using System.IO;
using HabilisTerminal.Core.Plugins;
using HabilisTerminal.Plugins;

namespace HabilisTerminal.App.Plugins;

/// <summary>What the window does for plugins: viewing, re-listing their panels, status messages.</summary>
internal interface IPluginWindow
{
    Task ViewFileAsync(string path, string? title);

    Task ViewTextAsync(string title, string text);

    void Refresh(IPanelProvider provider);

    void ShowStatus(string message);
}

/// <summary>One plugin's <see cref="IPluginHost"/> (docs/plugins.md §4).</summary>
internal sealed class PluginHostServices(PluginManifest manifest, IPluginDialogs dialogs, IPluginWindow window, string settingsDirectory, string tempRoot) : IPluginHost
{
    public Version HostVersion { get; } = typeof(PluginHostServices).Assembly.GetName().Version ?? new Version(0, 0);

    public IPluginDialogs Dialogs { get; } = dialogs;

    public IPluginSettings Settings { get; } = new PluginSettingsStore(settingsDirectory, manifest.Id);

    public string TempDirectory { get; } = PrepareTemp(Path.Combine(tempRoot, manifest.Id));

    public Task ViewFileAsync(string path, string? title = null) => window.ViewFileAsync(path, title);

    public Task ViewTextAsync(string title, string text) => window.ViewTextAsync(title, text);

    public void Refresh(IPanelProvider provider) => window.Refresh(provider);

    public void ShowStatus(string message) => window.ShowStatus(message);

    /// <summary>Emptied on start, so copies made for the viewer do not pile up.</summary>
    private static string PrepareTemp(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still open from a previous run stays; the folder is reused.
        }

        Directory.CreateDirectory(path);
        return path;
    }
}
