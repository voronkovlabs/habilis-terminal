using System.Text.Json.Nodes;

namespace HabilisTerminal.Plugins;

/// <summary>Services the host gives a plugin.</summary>
public interface IPluginHost
{
    /// <summary>The host's version.</summary>
    Version HostVersion { get; }

    /// <summary>Dialogs drawn by the host in its own look.</summary>
    IPluginDialogs Dialogs { get; }

    /// <summary>The plugin's settings. Never store secrets here.</summary>
    IPluginSettings Settings { get; }

    /// <summary>A temporary folder for this plugin (e.g. copies for the viewer); emptied when the host starts.</summary>
    string TempDirectory { get; }

    /// <summary>
    /// The folder the plugin runs from, with its plugin.json and other files (SDK 1.5). Use this to find them:
    /// during development it is a temporary copy, not where the plugin was built.
    /// </summary>
    string PluginDirectory => Path.GetDirectoryName(GetType().Assembly.Location) ?? AppContext.BaseDirectory;

    /// <summary>Opens a local file in the host's viewer.</summary>
    Task ViewFileAsync(string path, string? title = null);

    /// <summary>Opens text in the host's viewer (read-only).</summary>
    Task ViewTextAsync(string title, string text);

    /// <summary>Re-lists every open panel of this provider, e.g. after the settings changed.</summary>
    void Refresh(IPanelProvider provider);

    /// <summary>A short message in the panel's status line.</summary>
    void ShowStatus(string message);
}

/// <summary>
/// Per-plugin settings, stored by the host as JSON next to its own settings. Never put credentials, tokens or other
/// secrets here: the file is plain text.
/// </summary>
public interface IPluginSettings
{
    /// <summary>The stored settings (an empty object when nothing was saved yet).</summary>
    JsonObject Load();

    /// <summary>Replaces the stored settings.</summary>
    void Save(JsonObject settings);
}
