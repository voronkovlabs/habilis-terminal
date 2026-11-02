using System.Text.Json;
using System.Text.Json.Nodes;
using HabilisTerminal.Plugins;

namespace HabilisTerminal.Core.Plugins;

/// <summary>A plugin's settings as <c>plugins\&lt;id&gt;.json</c> in the settings folder (docs/plugins.md §4).</summary>
public sealed class PluginSettingsStore(string directory, string pluginId) : IPluginSettings
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly object _gate = new();

    public string FilePath { get; } = Path.Combine(directory, pluginId + ".json");

    public JsonObject Load()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject stored ? stored : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A broken file reads as empty settings; saving replaces it.
                return [];
            }
        }
    }

    public void Save(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, settings.ToJsonString(WriteOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
    }
}
