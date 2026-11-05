using System.Reflection;
using System.Text.Json;

namespace HabilisTerminal.Themes;

public sealed class ThemeLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Loads VS Code colour themes: JSONC (comments, trailing commas), <c>include</c> chains (the including theme
/// wins), <c>type</c> from the theme or its includes (SPEC §10).
/// </summary>
public static class ThemeLoader
{
    public const string DarkModern = "dark_modern.json";
    public const string LightModern = "light_modern.json";
    public const string HighContrastDark = "hc_black.json";
    public const string HighContrastLight = "hc_light.json";

    private const int MaxIncludeDepth = 16;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Names of the themes shipped inside the assembly (from the MIT-licensed VS Code repository).</summary>
    public static IReadOnlyList<string> BuiltInThemes { get; } =
        typeof(ThemeLoader).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..])
            .ToArray();

    private static string ResourcePrefix => typeof(ThemeLoader).Namespace + ".Assets.themes.";

    /// <summary>
    /// The type VS Code gives the built-in themes in its extension manifest (<c>uiTheme</c>); the files themselves
    /// do not always say.
    /// </summary>
    private static readonly Dictionary<string, ThemeType> BuiltInTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dark_modern.json"] = ThemeType.Dark,
        ["dark_plus.json"] = ThemeType.Dark,
        ["dark_vs.json"] = ThemeType.Dark,
        ["light_modern.json"] = ThemeType.Light,
        ["light_plus.json"] = ThemeType.Light,
        ["light_vs.json"] = ThemeType.Light,
        ["hc_black.json"] = ThemeType.HighContrast,
        ["hc_light.json"] = ThemeType.HighContrastLight,
        ["far.json"] = ThemeType.Dark,
        ["matrix.json"] = ThemeType.Dark,
    };

    /// <summary>
    /// Built-in themes with a fixed palette that the Windows accent colour must not tint (the Far Manager theme's teal
    /// cursor, issue #42; the Matrix theme's green, issue #123).
    /// </summary>
    public static bool KeepsOwnAccent(string fileName) => 
        string.Equals(fileName, "far.json", StringComparison.OrdinalIgnoreCase) || string.Equals(fileName, "matrix.json", StringComparison.OrdinalIgnoreCase);

    public static ColorTheme LoadBuiltIn(string fileName) =>
        Load(
            fileName,
            name => ReadResource(name) ?? throw new ThemeLoadException($"Built-in theme '{name}' not found."),
            BuiltInTypes.TryGetValue(fileName, out var type) ? type : null);

    public static ColorTheme LoadFile(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        return Load(Path.GetFileName(full), name =>
        {
            var candidate = Path.GetFullPath(Path.Combine(directory, name));
            try
            {
                return File.ReadAllText(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ThemeLoadException($"Cannot read '{candidate}': {ex.Message}", ex);
            }
        });
    }

    /// <summary>Loads a theme whose files are provided by <paramref name="readFile"/> (relative names, as written in <c>include</c>).</summary>
    /// <param name="typeHint">Used when no file in the chain declares <c>type</c> (as an extension manifest would).</param>
    public static ColorTheme Load(string fileName, Func<string, string> readFile, ThemeType? typeHint = null)
    {
        var colors = new Dictionary<string, ThemeColor>(StringComparer.OrdinalIgnoreCase);
        var tokenColors = new List<JsonElement>();
        ThemeType? type = null;
        string? name = null;

        // Walk the chain from the theme down to its base, keeping the first (most specific) value for each key.
        var current = fileName;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var layers = new List<JsonElement>();
        for (var depth = 0; current is not null; depth++)
        {
            var normalized = Normalize(current);
            if (depth >= MaxIncludeDepth || !visited.Add(normalized))
            {
                throw new ThemeLoadException($"Include cycle or too deep at '{current}'.");
            }

            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(readFile(normalized), JsonOptions);
                root = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new ThemeLoadException($"'{current}' is not valid JSON: {ex.Message}", ex);
            }

            layers.Add(root);
            name ??= root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            type ??= root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? ParseType(t.GetString()) : null;
            current = root.TryGetProperty("include", out var include) && include.ValueKind == JsonValueKind.String
                ? Combine(normalized, include.GetString()!)
                : null;
        }

        // Base first, so more specific layers override colours and add token rules after (later rules win in TextMate).
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            var layer = layers[i];
            if (layer.TryGetProperty("colors", out var layerColors) && layerColors.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in layerColors.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String && ThemeColor.TryParse(property.Value.GetString(), out var color))
                    {
                        colors[property.Name] = color;
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Null)
                    {
                        colors.Remove(property.Name);
                    }
                }
            }

            if (layer.TryGetProperty("tokenColors", out var tokens) && tokens.ValueKind == JsonValueKind.Array)
            {
                tokenColors.AddRange(tokens.EnumerateArray());
            }
        }

        return new ColorTheme(name ?? Path.GetFileNameWithoutExtension(fileName), type ?? typeHint ?? GuessType(colors), colors, tokenColors);
    }

    public static ThemeType? ParseType(string? value) => value?.ToLowerInvariant() switch
    {
        "dark" or "vs-dark" => ThemeType.Dark,
        "light" or "vs" => ThemeType.Light,
        "hc" or "hc-black" or "hcdark" => ThemeType.HighContrast,
        "hclight" or "hc-light" => ThemeType.HighContrastLight,
        _ => null,
    };

    private static ThemeType GuessType(Dictionary<string, ThemeColor> colors) =>
        colors.TryGetValue("editor.background", out var background) && background.Luminance > 0.5 ? ThemeType.Light : ThemeType.Dark;

    private static string Normalize(string name) => name.Replace('\\', '/').TrimStart('.', '/');

    private static string Combine(string from, string include)
    {
        var directory = Path.GetDirectoryName(from.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var combined = Path.Combine(directory, include.Replace('/', Path.DirectorySeparatorChar));
        return Normalize(combined.Replace(Path.DirectorySeparatorChar, '/'));
    }

    private static string? ReadResource(string fileName)
    {
        var resource = ResourcePrefix + Path.GetFileName(fileName);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
