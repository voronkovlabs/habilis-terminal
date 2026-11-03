using System.Text.Json;

namespace HabilisTerminal.Core.Settings;

/// <summary>
/// Version comparison for "Check for updates" (issue #71) where Velopack does not apply (portable and development
/// builds): the latest GitHub release's tag against the running version.
/// </summary>
public static class ReleaseVersions
{
    /// <summary>"v0.1.7", "0.1.7" or "0.1.7+abc" → 0.1.7; null for anything else.</summary>
    public static Version? Parse(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim().TrimStart('v', 'V');
        var end = trimmed.IndexOfAny(['+', '-', ' ']);
        if (end >= 0)
        {
            trimmed = trimmed[..end];
        }

        return Version.TryParse(trimmed, out var version) ? version : null;
    }

    /// <summary>The newer release, or null when <paramref name="current"/> is as new or the tag cannot be read.</summary>
    public static Version? Newer(string? latestTag, string? current)
    {
        var latest = Parse(latestTag);
        var running = Parse(current);
        return latest is not null && running is not null && Normalize(latest) > Normalize(running) ? latest : null;
    }

    /// <summary>The <c>tag_name</c> of GitHub's "latest release" response, or null.</summary>
    public static string? TagOf(string releaseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(releaseJson);
            return document.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
