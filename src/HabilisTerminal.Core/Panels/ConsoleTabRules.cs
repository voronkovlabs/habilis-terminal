namespace HabilisTerminal.Core.Panels;

/// <summary>Which console tab comes next, and when closing needs a question (issue #119).</summary>
public static class ConsoleTabRules
{
    /// <summary>A tab's narrowest width while the strip has room, and when it has not (issue #121).</summary>
    public const double WideMinimum = 135;

    public const double NarrowMinimum = 45;

    /// <summary>However much room there is, a tab is no wider than this.</summary>
    public const double Widest = 320;

    /// <summary>
    /// A tab's width limits: the minimum falls from <see cref="WideMinimum"/> to <see cref="NarrowMinimum"/> as tabs
    /// are added, and a tab never takes more than its share while the others need the room.
    /// </summary>
    public static (double Minimum, double Maximum) TabWidths(double available, int count)
    {
        var share = count > 0 ? available / count : available;
        var minimum = Math.Clamp(share, NarrowMinimum, WideMinimum);
        return (minimum, Math.Max(minimum, Math.Min(share, Widest)));
    }

    /// <summary>Even at their narrowest the tabs do not fit: the strip scrolls.</summary>
    public static bool Scrolls(double available, int count) => count * NarrowMinimum > available;

    /// <summary>"0:07", "12:30", "1:02:03".</summary>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        var seconds = (long)Math.Max(0, elapsed.TotalSeconds);
        var (hours, minutes, rest) = (seconds / 3600, seconds / 60 % 60, seconds % 60);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return hours > 0 ? string.Create(culture, $"{hours}:{minutes:00}:{rest:00}") : string.Create(culture, $"{minutes}:{rest:00}");
    }

    /// <summary>The folder above <paramref name="folder"/>, with the home folder as "~"; empty at the top of a drive.</summary>
    public static string ParentForDisplay(string folder, string home)
    {
        var parent = Path.GetDirectoryName(folder.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(parent))
        {
            return string.Empty;
        }

        var homeRoot = home.TrimEnd('\\', '/');
        if (homeRoot.Length > 0 && parent.StartsWith(homeRoot, StringComparison.OrdinalIgnoreCase) && (parent.Length == homeRoot.Length || parent[homeRoot.Length] == '\\'))
        {
            return "~" + parent[homeRoot.Length..];
        }

        return parent;
    }

    /// <summary>The tab Ctrl+Tab (+1) or Ctrl+Shift+Tab (-1) goes to; it wraps around.</summary>
    public static int Next(int current, int count, int direction) => ((current + direction) % count + count) % count;

    /// <summary>The tab shown after the one at <paramref name="closedIndex"/> was removed: the one that took its place, or the new last.</summary>
    public static int AfterClosing(int closedIndex, int remaining) => Math.Min(closedIndex, remaining - 1);

    /// <summary>The first colour no tab has; with all of them taken they go round again.</summary>
    public static int FreeColor(IReadOnlyCollection<int> used, int colorCount)
    {
        for (var index = 0; index < colorCount; index++)
        {
            if (!used.Contains(index))
            {
                return index;
            }
        }

        return used.Count % colorCount;
    }

    /// <summary>Closing stops running commands, so it asks when there are any.</summary>
    public static bool AskBeforeClosing(int runningCommands) => runningCommands > 0;
}
