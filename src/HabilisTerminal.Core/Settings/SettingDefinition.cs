using System.Text.Json;

namespace HabilisTerminal.Core.Settings;

public enum SettingKind
{
    Toggle,
    Number,
    Text,
    Choice,
}

/// <summary>One setting: its key in settings.json, where the Settings page shows it, its type and default (SPEC §14).</summary>
public sealed record SettingDefinition(
    string Key,
    string Category,
    string Title,
    string Description,
    SettingKind Kind,
    object Default,
    IReadOnlyList<string>? Values = null,
    int Minimum = int.MinValue,
    int Maximum = int.MaxValue,
    bool RequiresRestart = false)
{
    /// <summary>Checks a JSON value against the definition; returns the CLR value or an error message.</summary>
    public (object? Value, string? Error) Convert(JsonElement element)
    {
        switch (Kind)
        {
            case SettingKind.Toggle when element.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return (element.GetBoolean(), null);
            case SettingKind.Number when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number):
                return number < Minimum || number > Maximum
                    ? (null, $"'{Key}' must be between {Minimum} and {Maximum}.")
                    : (number, null);
            case SettingKind.Text when element.ValueKind == JsonValueKind.String:
                return (element.GetString()!, null);
            case SettingKind.Choice when element.ValueKind == JsonValueKind.String:
                var text = element.GetString()!;
                var match = Values?.FirstOrDefault(v => string.Equals(v, text, StringComparison.OrdinalIgnoreCase));
                return match is null ? (null, $"'{Key}' must be one of: {string.Join(", ", Values ?? [])}.") : (match, null);
            default:
                return (null, $"'{Key}' must be {KindName}.");
        }
    }

    private string KindName => Kind switch
    {
        SettingKind.Toggle => "true or false",
        SettingKind.Number => "a whole number",
        _ => "a string",
    };
}
