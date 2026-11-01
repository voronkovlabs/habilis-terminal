using System.Text.Json;
using System.Text.Json.Nodes;

namespace HabilisTerminal.Core.Settings;

/// <summary>
/// Every setting with its default (SPEC §14). Defaults live here, settings.json holds only changed values; the JSON
/// Schema written next to settings.json and the Settings page are both generated from this list.
/// </summary>
public static class SettingsCatalog
{
    public const string Appearance = "Appearance";
    public const string Panels = "Panels";
    public const string Terminal = "Terminal";
    public const string FileOperations = "File operations";
    public const string Confirmations = "Confirmations";
    public const string Viewer = "Viewer";
    public const string Editor = "Editor";

    public static IReadOnlyList<string> Categories { get; } = [Appearance, Panels, Terminal, FileOperations, Confirmations, Viewer, Editor];

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new("theme", Appearance, "Color Theme",
            "\"auto\" follows the Windows light / dark mode. Otherwise a built-in theme name or the file name of a VS Code color theme in the themes folder.",
            SettingKind.Text, "auto", ["auto", "dark_modern", "light_modern", "dark_plus", "light_plus", "dark_vs", "light_vs", "hc_black", "hc_light"]),
        new("useAccentColor", Appearance, "Use Accent Color", "Tint focus and selection with the Windows accent color.", SettingKind.Toggle, true),
        new("iconTheme", Appearance, "File Icon Theme", "Icons in the file panels.", SettingKind.Choice, "seti", ["seti", "material"]),

        new("panels.fontFamily", Panels, "Font Family", "Font of the file panels.", SettingKind.Text, "Segoe UI"),
        new("panels.fontSize", Panels, "Font Size", "Font size of the file panels, in pixels.", SettingKind.Number, 13, Minimum: 8, Maximum: 32),
        new("panels.showHidden", Panels, "Show Hidden Files", "Show hidden and system files (Ctrl+H toggles it for a panel).", SettingKind.Toggle, true),

        new("terminal.fontFamily", Terminal, "Font Family", "Font of the terminal.", SettingKind.Text, "Cascadia Mono"),
        new("terminal.fontSize", Terminal, "Font Size", "Font size of the terminal, in points.", SettingKind.Number, 11, Minimum: 6, Maximum: 48),
        new("terminal.defaultRows", Terminal, "Default Rows", "Terminal height in rows below the panels on first start.", SettingKind.Number, 12, Minimum: 2, Maximum: 200),
        new("terminal.multilinePasteWarning", Terminal, "Multi-line Paste Warning", "Ask before pasting text with several lines.", SettingKind.Toggle, true),
        new("terminal.scrollback", Terminal, "Scrollback", "Lines kept in the terminal history.", SettingKind.Number, 9001, Minimum: 0, Maximum: 100000, RequiresRestart: true),
        new("shell.path", Terminal, "Shell Path", "Path to pwsh.exe. Empty finds PowerShell 7 automatically.", SettingKind.Text, "", RequiresRestart: true),
        new("shell.args", Terminal, "Shell Arguments", "Extra arguments for pwsh, e.g. -NoProfile.", SettingKind.Text, "", RequiresRestart: true),
        new("shell.integration", Terminal, "Shell Integration",
            "Load the integration script that keeps panels and the shell in sync. Without it the panels do not follow cd.", SettingKind.Toggle, true, RequiresRestart: true),
        new("shell.initCommand", Terminal, "Init Command", "PowerShell code run before the integration script, after the profile.", SettingKind.Text, "", RequiresRestart: true),

        new("deleteToRecycleBin", FileOperations, "Delete to Recycle Bin", "F8 and Del move items to the Recycle Bin; Shift+Del always deletes permanently.", SettingKind.Toggle, true),

        new("confirmExit", Confirmations, "Exit", "Ask before exiting with F10.", SettingKind.Toggle, true),
        new("confirmDelete", Confirmations, "Delete", "Ask before deleting.", SettingKind.Toggle, true),

        new("viewer.wrap", Viewer, "Word Wrap", "Wrap long lines when a file opens (F2 toggles it).", SettingKind.Toggle, false),
        new("viewer.fontFamily", Viewer, "Font Family", "Font of the viewer.", SettingKind.Text, "Cascadia Mono"),
        new("viewer.fontSize", Viewer, "Font Size", "Font size of the viewer, in pixels.", SettingKind.Number, 13, Minimum: 6, Maximum: 48),

        new("editor.syntaxHighlighting", Editor, "Syntax Highlighting", "Color the text by file type.", SettingKind.Toggle, true),
        new("editor.largeFileThresholdMB", Editor, "Large File Threshold", "Text files larger than this many megabytes ask before opening in the editor.", SettingKind.Number, 50, Minimum: 1, Maximum: 2000),
        new("editor.fontFamily", Editor, "Font Family", "Font of the editor.", SettingKind.Text, "Cascadia Mono"),
        new("editor.fontSize", Editor, "Font Size", "Font size of the editor, in pixels.", SettingKind.Number, 13, Minimum: 6, Maximum: 48),
        new("editor.tabSize", Editor, "Tab Size", "Width of a tab in spaces.", SettingKind.Number, 4, Minimum: 1, Maximum: 16),
        new("editor.insertSpaces", Editor, "Insert Spaces", "Tab inserts spaces instead of a tab character.", SettingKind.Toggle, false),
    ];

    private static readonly Dictionary<string, SettingDefinition> ByKey = All.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static SettingDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>JSON Schema (draft-07) for settings.json, so editors offer completion and validation.</summary>
    public static string Schema()
    {
        var properties = new JsonObject();
        foreach (var definition in All)
        {
            var property = new JsonObject
            {
                ["description"] = definition.Description + (definition.RequiresRestart ? " Applies after restart." : string.Empty),
                ["default"] = definition.Default switch
                {
                    bool flag => JsonValue.Create(flag),
                    int number => JsonValue.Create(number),
                    var other => JsonValue.Create((string)other),
                },
            };
            switch (definition.Kind)
            {
                case SettingKind.Toggle:
                    property["type"] = "boolean";
                    break;
                case SettingKind.Number:
                    property["type"] = "integer";
                    if (definition.Minimum != int.MinValue)
                    {
                        property["minimum"] = definition.Minimum;
                    }

                    if (definition.Maximum != int.MaxValue)
                    {
                        property["maximum"] = definition.Maximum;
                    }

                    break;
                case SettingKind.Choice:
                    property["type"] = "string";
                    property["enum"] = new JsonArray([.. definition.Values!.Select(v => (JsonNode)v)]);
                    break;
                default:
                    property["type"] = "string";
                    if (definition.Values is { } examples)
                    {
                        property["examples"] = new JsonArray([.. examples.Select(v => (JsonNode)v)]);
                    }

                    break;
            }

            properties[definition.Key] = property;
        }

        var schema = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["title"] = "Habilis Terminal settings",
            ["type"] = "object",
            ["properties"] = properties,
        };
        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
