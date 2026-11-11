using System.Text.Json;
using System.Text.RegularExpressions;

namespace HabilisTerminal.Core.Plugins;

/// <summary>A plugin's <c>plugin.json</c> (docs/plugins.md §3).</summary>
public sealed partial record PluginManifest(string Id, string Name, string Version, string Assembly, string Sdk)
{
    public const string FileName = "plugin.json";

    /// <summary>
    /// False keeps the plugin's code in memory after an unload, for code that cannot run in a collectible load context
    /// (optional, default true). Reloading still works; the old copy stays until the app restarts.
    /// </summary>
    public bool Collectible { get; init; } = true;

    /// <summary>The SDK version this host implements.</summary>
    public static Version HostSdkVersion { get; } = new(1, 8);

    /// <summary>Reads and checks a manifest; the exception message is shown to the user.</summary>
    /// <exception cref="InvalidDataException">Missing or invalid fields.</exception>
    public static PluginManifest Parse(string json)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }).RootElement;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FileName} is not valid JSON: {ex.Message}", ex);
        }

        string Read(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                ? text
                : throw new InvalidDataException($"{FileName} has no \"{name}\".");

        var manifest = new PluginManifest(Read("id"), Read("name"), Read("version"), Read("assembly"), Read("sdk"))
        {
            Collectible = !root.TryGetProperty("collectible", out var collectible) || collectible.ValueKind != JsonValueKind.False,
        };
        if (!IdPattern().IsMatch(manifest.Id))
        {
            throw new InvalidDataException($"Plugin id \"{manifest.Id}\" may contain only letters, digits, '.', '-' and '_'.");
        }

        if (manifest.Assembly.IndexOfAny(['\\', '/', ':']) >= 0 || !manifest.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"\"assembly\" must be a .dll file name in the plugin folder, not \"{manifest.Assembly}\".");
        }

        manifest.CheckSdk();
        return manifest;
    }

    /// <summary>Same major version as the host, and not a newer minor version.</summary>
    private void CheckSdk()
    {
        if (!System.Version.TryParse(Sdk.Contains('.', StringComparison.Ordinal) ? Sdk : Sdk + ".0", out var wanted))
        {
            throw new InvalidDataException($"\"sdk\" must be a version such as \"1.0\", not \"{Sdk}\".");
        }

        if (wanted.Major != HostSdkVersion.Major || wanted.Minor > HostSdkVersion.Minor)
        {
            throw new InvalidDataException($"The plugin needs SDK {wanted.Major}.{wanted.Minor}; this Habilis Terminal provides SDK {HostSdkVersion.Major}.{HostSdkVersion.Minor}.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex IdPattern();
}
