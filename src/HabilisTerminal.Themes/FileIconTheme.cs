using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace HabilisTerminal.Themes;

/// <summary>One filled shape: WPF path markup and its colour.</summary>
public sealed record IconShape(string Data, ThemeColor Fill);

/// <summary>A vector icon: view box and shapes (converted at build time by tools/IconBuilder).</summary>
public sealed record IconDefinition(string Id, double X, double Y, double Width, double Height, IReadOnlyList<IconShape> Shapes);

/// <summary>
/// A VS Code file icon theme (SPEC §10): <c>iconDefinitions</c>, <c>file</c>, <c>folder</c>, <c>fileNames</c>,
/// <c>fileExtensions</c>, <c>folderNames</c>, <c>languageIds</c> and the <c>light</c> overrides, resolved like VS Code:
/// file name, then extensions (longest first), then language, then the default icon.
/// </summary>
public sealed class FileIconTheme
{
    public const string Seti = "seti";
    public const string Material = "material";

    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly Lazy<LanguageTable> Languages = new(LanguageTable.LoadBuiltIn);

    private readonly Dictionary<string, IconDefinition> _definitions;
    private readonly Associations _main;
    private readonly Associations? _light;

    private FileIconTheme(string name, Dictionary<string, IconDefinition> definitions, Associations main, Associations? light)
    {
        Name = name;
        _definitions = definitions;
        _main = main;
        _light = light;
    }

    public string Name { get; }

    public int Count => _definitions.Count;

    public static IReadOnlyList<string> BuiltInThemes { get; } = [Seti, Material];

    public static FileIconTheme LoadBuiltIn(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"{typeof(FileIconTheme).Namespace}.Assets.icons.{name}.json")
            ?? throw new ThemeLoadException($"Built-in icon theme '{name}' not found.");
        return Load(stream);
    }

    public static FileIconTheme Load(Stream stream)
    {
        using var document = JsonDocument.Parse(stream, JsonOptions);
        var root = document.RootElement;
        var definitions = new Dictionary<string, IconDefinition>(StringComparer.Ordinal);
        foreach (var property in root.GetProperty("iconDefinitions").EnumerateObject())
        {
            var box = property.Value.GetProperty("viewBox").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var shapes = property.Value.GetProperty("paths").EnumerateArray()
                .Select(p => new IconShape(p.GetProperty("d").GetString()!, ThemeColor.TryParse(p.GetProperty("fill").GetString(), out var c) ? c : ThemeColor.FromRgb(0x808080)))
                .ToArray();
            definitions[property.Name] = new IconDefinition(property.Name, box[0], box[1], box[2], box[3], shapes);
        }

        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "icons" : "icons";
        var light = root.TryGetProperty("light", out var lightElement) ? Associations.Read(lightElement) : null;
        return new FileIconTheme(name, definitions, Associations.Read(root), light);
    }

    public IconDefinition? ResolveFile(string fileName, bool light = false)
    {
        var name = fileName.ToLowerInvariant();
        foreach (var associations in Layers(light))
        {
            if (associations.FileNames.TryGetValue(name, out var id) && _definitions.TryGetValue(id, out var byName))
            {
                return byName;
            }
        }

        foreach (var extension in Extensions(name))
        {
            foreach (var associations in Layers(light))
            {
                if (associations.FileExtensions.TryGetValue(extension, out var id) && _definitions.TryGetValue(id, out var byExtension))
                {
                    return byExtension;
                }
            }
        }

        if (Languages.Value.LanguageOf(name) is { } language)
        {
            foreach (var associations in Layers(light))
            {
                if (associations.LanguageIds.TryGetValue(language, out var id) && _definitions.TryGetValue(id, out var byLanguage))
                {
                    return byLanguage;
                }
            }
        }

        return Default(light, a => a.File);
    }

    /// <summary>The folder icon, or null when the theme has none (Seti): the caller draws a generic folder.</summary>
    public IconDefinition? ResolveFolder(string folderName, bool light = false)
    {
        var name = folderName.ToLowerInvariant();
        foreach (var associations in Layers(light))
        {
            if (associations.FolderNames.TryGetValue(name, out var id) && _definitions.TryGetValue(id, out var byName))
            {
                return byName;
            }
        }

        return Default(light, a => a.Folder);
    }

    /// <summary>"a.test.ts" → "test.ts", "ts" (longest first, as VS Code matches).</summary>
    internal static IEnumerable<string> Extensions(string name)
    {
        for (var dot = name.IndexOf('.', 1); dot > 0 && dot < name.Length - 1; dot = name.IndexOf('.', dot + 1))
        {
            yield return name[(dot + 1)..];
        }
    }

    private IEnumerable<Associations> Layers(bool light) => light && _light is not null ? [_light, _main] : [_main];

    private IconDefinition? Default(bool light, Func<Associations, string?> select)
    {
        foreach (var associations in Layers(light))
        {
            if (select(associations) is { } id && _definitions.TryGetValue(id, out var definition))
            {
                return definition;
            }
        }

        return null;
    }

    private sealed record Associations(
        string? File,
        string? Folder,
        Dictionary<string, string> FileNames,
        Dictionary<string, string> FileExtensions,
        Dictionary<string, string> FolderNames,
        Dictionary<string, string> LanguageIds)
    {
        public static Associations Read(JsonElement element) => new(
            String(element, "file"),
            String(element, "folder"),
            Map(element, "fileNames"),
            Map(element, "fileExtensions"),
            Map(element, "folderNames"),
            Map(element, "languageIds"));

        private static string? String(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

        private static Dictionary<string, string> Map(JsonElement element, string name)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Value.GetString() is { } id)
                    {
                        map[property.Name.ToLower(CultureInfo.InvariantCulture)] = id;
                    }
                }
            }

            return map;
        }
    }
}

/// <summary>File name / extension → VS Code language id (from the built-in extensions' manifests).</summary>
public sealed class LanguageTable
{
    private readonly Dictionary<string, string> _extensions;
    private readonly Dictionary<string, string> _fileNames;

    private LanguageTable(Dictionary<string, string> extensions, Dictionary<string, string> fileNames)
    {
        _extensions = extensions;
        _fileNames = fileNames;
    }

    public static LanguageTable LoadBuiltIn()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"{typeof(LanguageTable).Namespace}.Assets.icons.languages.json")
            ?? throw new ThemeLoadException("Language table not found.");
        using var document = JsonDocument.Parse(stream);
        static Dictionary<string, string> Read(JsonElement element) =>
            element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        return new LanguageTable(Read(document.RootElement.GetProperty("extensions")), Read(document.RootElement.GetProperty("fileNames")));
    }

    /// <summary>The language of a lower-case file name, or null.</summary>
    public string? LanguageOf(string lowerName)
    {
        if (_fileNames.TryGetValue(lowerName, out var byName))
        {
            return byName;
        }

        foreach (var extension in FileIconTheme.Extensions(lowerName))
        {
            if (_extensions.TryGetValue(extension, out var byExtension))
            {
                return byExtension;
            }
        }

        return null;
    }
}
