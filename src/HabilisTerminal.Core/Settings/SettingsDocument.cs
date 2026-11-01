using System.Text;
using System.Text.Json;

namespace HabilisTerminal.Core.Settings;

/// <summary>A problem in settings.json; <see cref="Line"/> is 1-based.</summary>
public sealed record SettingsError(int Line, string Message)
{
    public override string ToString() => $"settings.json, line {Line}: {Message}";
}

/// <summary>Values read from settings.json. <see cref="IsSyntaxError"/> means nothing could be read.</summary>
public sealed record SettingsParseResult(
    IReadOnlyDictionary<string, object> Values,
    IReadOnlySet<string> InvalidKeys,
    IReadOnlyList<SettingsError> Errors,
    bool IsSyntaxError);

/// <summary>
/// Reads and edits settings.json as JSONC (comments, trailing commas). Edits change only the text of one property, so
/// the user's comments and formatting survive changes made from the Settings page.
/// </summary>
public static class SettingsDocument
{
    private const string Indent = "    ";

    private static readonly JsonReaderOptions ReaderOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static SettingsParseResult Parse(string text)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<SettingsError>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SettingsParseResult(values, invalid, errors, false);
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        List<PropertySpan> properties;
        try
        {
            properties = Scan(bytes);
        }
        catch (JsonException ex)
        {
            errors.Add(new SettingsError((int)(ex.LineNumber ?? 0) + 1, FirstSentence(ex.Message)));
            return new SettingsParseResult(values, invalid, errors, true);
        }
        catch (FormatException ex)
        {
            errors.Add(new SettingsError(1, ex.Message));
            return new SettingsParseResult(values, invalid, errors, true);
        }

        foreach (var property in properties)
        {
            if (property.Key.StartsWith('$'))
            {
                continue;
            }

            var definition = SettingsCatalog.Find(property.Key);
            if (definition is null)
            {
                errors.Add(new SettingsError(property.Line, $"Unknown setting '{property.Key}'."));
                continue;
            }

            using var document = JsonDocument.Parse(
                bytes.AsMemory(property.ValueStart, property.ValueEnd - property.ValueStart),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var (value, error) = definition.Convert(document.RootElement);
            if (error is not null)
            {
                errors.Add(new SettingsError(property.Line, error));
                invalid.Add(property.Key);
            }
            else
            {
                values[property.Key] = value!;
            }
        }

        return new SettingsParseResult(values, invalid, errors, false);
    }

    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/>, or removes it when the value is null.</summary>
    /// <exception cref="JsonException">The text is not valid JSONC.</exception>
    public static string SetValue(string text, string key, object? value)
    {
        var json = value is null ? null : JsonSerializer.Serialize(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return json is null ? text : "{\n" + Indent + JsonSerializer.Serialize(key) + ": " + json + "\n}\n";
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var properties = Scan(bytes);
        var existing = properties.LastOrDefault(p => p.Key == key);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        if (existing is not null)
        {
            if (json is not null)
            {
                return Splice(bytes, existing.ValueStart, existing.ValueEnd, json);
            }

            var (start, end) = RemovalRange(bytes, existing);
            return Splice(bytes, start, end, string.Empty);
        }

        if (json is null)
        {
            return text;
        }

        var entry = Indent + JsonSerializer.Serialize(key) + ": " + json;
        if (properties.Count == 0)
        {
            // Right after the opening brace.
            var open = Array.IndexOf(bytes, (byte)'{');
            return Splice(bytes, open + 1, open + 1, newLine + entry + newLine);
        }

        var last = properties[^1];
        var afterValue = SkipWhitespace(bytes, last.ValueEnd);
        if (afterValue < bytes.Length && bytes[afterValue] == ',')
        {
            return Splice(bytes, afterValue + 1, afterValue + 1, newLine + entry);
        }

        return Splice(bytes, last.ValueEnd, last.ValueEnd, "," + newLine + entry);
    }

    private static (int Start, int End) RemovalRange(byte[] bytes, PropertySpan property)
    {
        var lineStart = property.NameStart;
        while (lineStart > 0 && bytes[lineStart - 1] is (byte)' ' or (byte)'\t')
        {
            lineStart--;
        }

        var end = SkipWhitespace(bytes, property.ValueEnd, stopAtNewLine: true);
        if (end < bytes.Length && bytes[end] == ',')
        {
            end = SkipWhitespace(bytes, end + 1, stopAtNewLine: true);
        }

        var startsLine = lineStart == 0 || bytes[lineStart - 1] == '\n';
        var endsLine = end >= bytes.Length || bytes[end] is (byte)'\r' or (byte)'\n';
        if (!startsLine || !endsLine)
        {
            return (property.NameStart, end);
        }

        if (end < bytes.Length && bytes[end] == '\r')
        {
            end++;
        }

        if (end < bytes.Length && bytes[end] == '\n')
        {
            end++;
        }

        return (lineStart, end);
    }

    private static int SkipWhitespace(byte[] bytes, int index, bool stopAtNewLine = false)
    {
        while (index < bytes.Length && bytes[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            if (stopAtNewLine && bytes[index] is (byte)'\r' or (byte)'\n')
            {
                break;
            }

            index++;
        }

        return index;
    }

    private static string Splice(byte[] bytes, int start, int end, string replacement) =>
        Encoding.UTF8.GetString(bytes, 0, start) + replacement + Encoding.UTF8.GetString(bytes, end, bytes.Length - end);

    /// <summary>Top-level properties with byte ranges of their names and values.</summary>
    private static List<PropertySpan> Scan(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, ReaderOptions);
        var properties = new List<PropertySpan>();
        if (!reader.Read())
        {
            return properties;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new FormatException("settings.json must contain a JSON object.");
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
            {
                break;
            }

            var nameStart = (int)reader.TokenStartIndex;
            var key = reader.GetString()!;
            reader.Read();
            var valueStart = (int)reader.TokenStartIndex;
            reader.Skip();
            var valueEnd = (int)reader.BytesConsumed;
            properties.Add(new PropertySpan(key, nameStart, valueStart, valueEnd, LineOf(bytes, nameStart)));
        }

        // Anything after the root object must be whitespace or comments; the reader throws otherwise.
        while (reader.Read())
        {
        }

        return properties;
    }

    private static int LineOf(byte[] bytes, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (bytes[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string FirstSentence(string message)
    {
        var cut = message.IndexOf(" LineNumber", StringComparison.Ordinal);
        return (cut > 0 ? message[..cut] : message).Trim();
    }

    private sealed record PropertySpan(string Key, int NameStart, int ValueStart, int ValueEnd, int Line);
}
