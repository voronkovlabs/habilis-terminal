namespace HabilisTerminal.Core.Panels;

/// <summary>Which console tab comes next, and when closing needs a question (issue #119).</summary>
public static class ConsoleTabRules
{
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
