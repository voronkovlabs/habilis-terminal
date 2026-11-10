namespace HabilisTerminal.Core.Plugins;

/// <summary>What the host reads from a plugin panel's location (an address such as <c>s3://b/x/y/</c> or <c>gh:me/app/main</c>).</summary>
public static class PluginLocations
{
    /// <summary>
    /// The names the cursor should land on after going up from <paramref name="location"/>: its last segment as written,
    /// then unescaped when a plugin writes it escaped (GitHub's <c>feature%2Fx</c> is the branch <c>feature/x</c>,
    /// issue #220). Empty at the top.
    /// </summary>
    public static IReadOnlyList<string> NamesAfterUp(string location)
    {
        ArgumentNullException.ThrowIfNull(location);
        var trimmed = location.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\', ':']);
        if (cut < 0 || cut >= trimmed.Length - 1)
        {
            return [];
        }

        var segment = trimmed[(cut + 1)..];
        if (!segment.Contains('%', StringComparison.Ordinal))
        {
            return [segment];
        }

        string unescaped;
        try
        {
            unescaped = Uri.UnescapeDataString(segment);
        }
        catch (UriFormatException)
        {
            return [segment];
        }

        return unescaped == segment ? [segment] : [segment, unescaped];
    }
}
