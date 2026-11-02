using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Habilis.Aws.Common;

/// <summary>Small text helpers shared by the AWS plugins.</summary>
public static class AwsText
{
    /// <summary>Wildcard match with * and ?, case-insensitive (queue exclusion patterns).</summary>
    public static bool MatchesWildcard(string name, string pattern)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(pattern);
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// "sqs:prod/eu-west-1" → ("prod", "eu-west-1"); "sqs:prod" → ("prod", null); "sqs:" → (null, null).
    /// The prefix is whatever comes before the first colon.
    /// </summary>
    public static (string? Profile, string? Region) ParseProfileAddress(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var colon = address.IndexOf(':', StringComparison.Ordinal);
        var rest = (colon >= 0 ? address[(colon + 1)..] : address).Trim().Trim('/');
        if (rest.Length == 0)
        {
            return (null, null);
        }

        var parts = rest.Split('/', 2, StringSplitOptions.TrimEntries);
        return (parts[0].Length > 0 ? parts[0] : null, parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
    }

    /// <summary>A duration as the console shows it: "2 h 05 min", "4 min 12 s", "38 s".</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours} h {duration.Minutes:00} min")
            : duration.TotalMinutes >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{duration.Minutes} min {duration.Seconds:00} s")
                : string.Create(CultureInfo.InvariantCulture, $"{duration.Seconds} s");
    }

    public static string Text(JsonObject settings, string key, string fallback) =>
        settings[key] is JsonValue value && value.TryGetValue(out string? text) && text is not null ? text : fallback;

    public static int Number(JsonObject settings, string key, int fallback) =>
        settings[key] is JsonValue value && value.TryGetValue(out int number) ? number : fallback;

    public static bool Flag(JsonObject settings, string key, bool fallback) =>
        settings[key] is JsonValue value && value.TryGetValue(out bool flag) ? flag : fallback;

    public static IReadOnlyList<string> List(JsonObject settings, string key, IReadOnlyList<string> fallback) =>
        settings[key] is JsonArray array ? array.Select(n => n?.GetValue<string>()).OfType<string>().ToList() : fallback;

    public static JsonArray ToJson(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}
