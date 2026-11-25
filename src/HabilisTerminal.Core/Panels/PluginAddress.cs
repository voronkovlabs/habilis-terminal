namespace HabilisTerminal.Core.Panels;

/// <summary>
/// "cd name" in the console while a plugin panel is active (issue #381): the name is relative to the panel's location,
/// as in Far. Works for locations shaped like "scheme://a/b/", the way S3 writes them.
/// </summary>
public static class PluginAddress
{
    /// <summary>
    /// The address <paramref name="relative"/> leads to from <paramref name="location"/>; null when the location has no
    /// "scheme://" part to resolve against. ".." goes up, "/" starts at the top; a final "/" or ".." keeps the result a
    /// folder, otherwise the last part may be a file and stays without one.
    /// </summary>
    public static string? Resolve(string location, string relative)
    {
        var marker = location.IndexOf("://", StringComparison.Ordinal);
        if (marker < 1)
        {
            return null;
        }

        var root = location[..(marker + 3)];
        var text = relative.Trim().Replace('\\', '/');
        var parts = text.StartsWith('/') ? [] : location[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (segment != ".")
            {
                parts.Add(segment);
            }
        }

        var folder = text.Length == 0 || text.EndsWith('/') || segments.Length == 0 || segments[^1] is ".." or ".";
        return parts.Count == 0 ? root : root + string.Join('/', parts) + (folder ? "/" : string.Empty);
    }
}
