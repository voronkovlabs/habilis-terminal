using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using IconBuilder;

// Build-time converter (SPEC §10): VS Code file icon themes → icon-theme JSON with WPF path data, plus the
// language → extensions table. Run manually when the source icon sets are updated:
//   dotnet run --project tools/IconBuilder -- <download dir> <output dir>
// <download dir> holds seti/ (vs-seti-icon-theme.json, seti.woff), material-icon-theme/package/ and languages/*.json.
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: IconBuilder <download dir> <output dir>");
    return 2;
}

var source = args[0];
var output = args[1];
Directory.CreateDirectory(output);
var json = new JsonSerializerOptions { WriteIndented = false };

var seti = BuildSeti(Path.Combine(source, "seti"));
File.WriteAllText(Path.Combine(output, "seti.json"), seti.ToJsonString(json));

var material = BuildMaterial(Path.Combine(source, "material-icon-theme", "package"));
File.WriteAllText(Path.Combine(output, "material.json"), material.ToJsonString(json));

var languages = BuildLanguages(Path.Combine(source, "languages"));
File.WriteAllText(Path.Combine(output, "languages.json"), languages.ToJsonString(json));

foreach (var file in Directory.GetFiles(output, "*.json"))
{
    Console.WriteLine($"{Path.GetFileName(file)}: {new FileInfo(file).Length / 1024.0:N0} KB");
}

return 0;

static JsonObject BuildSeti(string directory)
{
    var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "vs-seti-icon-theme.json")))!.AsObject();
    var fontPath = Path.Combine(Path.GetTempPath(), "habi-assets", "build", "seti.ttf");
    Directory.CreateDirectory(Path.GetDirectoryName(fontPath)!);
    File.WriteAllBytes(fontPath, Woff.ToSfnt(File.ReadAllBytes(Path.Combine(directory, "seti.woff"))));
    var typeface = new GlyphTypeface(new Uri(fontPath));

    const double em = 16;
    var fontScale = 1.0;
    if (manifest["fonts"]?[0]?["size"]?.GetValue<string>() is { } declared && declared.EndsWith('%') &&
        double.TryParse(declared.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
    {
        fontScale = percent / 100;
    }
    var definitions = new JsonObject();
    var missing = 0;
    foreach (var (id, node) in manifest["iconDefinitions"]!.AsObject())
    {
        var character = node!["fontCharacter"]!.GetValue<string>().TrimStart('\\');
        var codepoint = int.Parse(character, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (!typeface.CharacterToGlyphMap.TryGetValue(codepoint, out var glyph))
        {
            missing++;
            continue;
        }

        // Outline in em units with the origin on the baseline; the view box is the glyph cell, made square.
        var outline = typeface.GetGlyphOutline(glyph, em, em);
        var width = typeface.AdvanceWidths[glyph] * em;
        var height = typeface.Height * em;
        // VS Code draws the Seti font at the size the theme declares ("size": "150%"): a smaller view box = larger glyph.
        var size = Math.Max(width, height) / fontScale;
        var viewBox = new Rect((width - size) / 2, (-typeface.Baseline * em) + ((height - size) / 2), size, size);
        definitions[id] = Definition(viewBox, [(outline, node["fontColor"]?.GetValue<string>() ?? "#8B8B8B")]);
    }

    Console.WriteLine($"seti: {definitions.Count} icons, {missing} missing glyphs");
    return Theme("Seti", manifest, definitions);
}

static JsonObject BuildMaterial(string package)
{
    var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(package, "dist", "material-icons.json")))!.AsObject();
    var converter = new SvgIconConverter();
    var definitions = new JsonObject();
    var failed = 0;
    foreach (var (id, node) in manifest["iconDefinitions"]!.AsObject())
    {
        var relative = node!["iconPath"]!.GetValue<string>();
        var file = Path.GetFullPath(Path.Combine(package, "dist", relative));
        try
        {
            var (viewBox, paths) = converter.Convert(File.ReadAllText(file));
            definitions[id] = Definition(viewBox, paths.Select(p => (p.Geometry, ToHex(p.Fill))).ToArray());
        }
        catch (Exception ex) when (ex is FormatException or IOException or System.Xml.XmlException or InvalidOperationException)
        {
            failed++;
            Console.WriteLine($"  material {id}: {ex.Message}");
        }
    }

    Console.WriteLine($"material: {definitions.Count} icons, {failed} failed, {converter.Warnings} ignored SVG features");
    return Theme("Material Icon Theme", manifest, definitions);
}

static JsonObject BuildLanguages(string directory)
{
    var extensions = new SortedDictionary<string, string>(StringComparer.Ordinal);
    var fileNames = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (var file in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
    {
        var languages = JsonNode.Parse(File.ReadAllText(file))?["contributes"]?["languages"]?.AsArray();
        foreach (var language in languages ?? [])
        {
            var id = language?["id"]?.GetValue<string>();
            if (id is null)
            {
                continue;
            }

            foreach (var extension in language!["extensions"]?.AsArray() ?? [])
            {
                extensions.TryAdd(extension!.GetValue<string>().TrimStart('.').ToLowerInvariant(), id);
            }

            foreach (var name in language["filenames"]?.AsArray() ?? [])
            {
                fileNames.TryAdd(name!.GetValue<string>().ToLowerInvariant(), id);
            }
        }
    }

    Console.WriteLine($"languages: {extensions.Count} extensions, {fileNames.Count} file names");
    return new JsonObject
    {
        ["extensions"] = new JsonObject(extensions.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
        ["fileNames"] = new JsonObject(fileNames.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
    };
}

static JsonObject Definition(Rect viewBox, (Geometry Geometry, string Fill)[] paths) => new()
{
    ["viewBox"] = new JsonArray(Round(viewBox.X), Round(viewBox.Y), Round(viewBox.Width), Round(viewBox.Height)),
    ["paths"] = new JsonArray(paths.Select(p => (JsonNode)new JsonObject { ["d"] = PathWriter.Write(p.Geometry), ["fill"] = p.Fill }).ToArray()),
};

static double Round(double value) => Math.Round(value, 3);

static string ToHex(Color color) => color.A == 255 ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";

// Keeps the VS Code file icon theme structure; icon definitions carry path data instead of files / font glyphs.
static JsonObject Theme(string name, JsonObject manifest, JsonObject definitions)
{
    var theme = new JsonObject { ["name"] = name, ["iconDefinitions"] = definitions };
    foreach (var key in new[] { "file", "folder", "folderExpanded", "fileExtensions", "fileNames", "folderNames", "languageIds", "light" })
    {
        if (manifest[key] is { } value)
        {
            theme[key] = value.DeepClone();
        }
    }

    if (theme["light"] is JsonObject light)
    {
        foreach (var unused in new[] { "folderNamesExpanded", "rootFolderNames", "rootFolderNamesExpanded" })
        {
            light.Remove(unused);
        }
    }

    return theme;
}
