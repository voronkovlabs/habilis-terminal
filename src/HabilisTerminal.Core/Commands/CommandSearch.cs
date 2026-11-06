namespace HabilisTerminal.Core.Commands;

/// <summary>A command as found: what it matched is its title in the current context.</summary>
public sealed record RankedCommand(AppCommand Command, string Title);

/// <summary>The order of the command line (issue #155), as in VS Code: letters in order, word starts ranked first.</summary>
public static class CommandSearch
{
    private const int WordStartScore = 10;
    private const int NextToPreviousScore = 5;
    private const int LetterScore = 1;
    private const int RecentBonus = 3;

    /// <summary>
    /// How well the letters of <paramref name="query"/> match in order; null when they do not. Spaces in the query are
    /// ignored: "show hid" and "shid" look for the same letters.
    /// </summary>
    public static int? Score(string text, string query)
    {
        var total = 0;
        var at = 0;
        var previous = -2;
        foreach (var letter in query)
        {
            if (char.IsWhiteSpace(letter))
            {
                continue;
            }

            var found = BestFrom(text, letter, at, previous);
            if (found < 0)
            {
                return null;
            }

            var isWordStart = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
            total += isWordStart ? WordStartScore : found == previous + 1 ? NextToPreviousScore : LetterScore;
            previous = found;
            at = found + 1;
        }

        return total;
    }

    /// <summary>The letter next to the previous one, else the first word start with it, else its first place.</summary>
    private static int BestFrom(string text, char letter, int from, int previous)
    {
        var first = -1;
        for (var i = from; i < text.Length; i++)
        {
            if (char.ToUpperInvariant(text[i]) != char.ToUpperInvariant(letter))
            {
                continue;
            }

            if (i == previous + 1 || i == 0 || !char.IsLetterOrDigit(text[i - 1]))
            {
                return i;
            }

            if (first < 0)
            {
                first = i;
            }
        }

        return first;
    }

    /// <summary>
    /// With nothing typed: recent commands, most recent first, then the sections that matter here, then the rest. With a
    /// query: the best match first, by title or by key.
    /// </summary>
    public static IReadOnlyList<RankedCommand> Rank(string query, IEnumerable<AppCommand> commands, CommandContext context, IReadOnlyList<string> recent, IReadOnlyList<string> sectionsFirst)
    {
        var ranked = commands.Where(c => c.AppliesWhen(context)).Select(c => new RankedCommand(c, c.TitleIn(context))).ToList();
        int RecentIndex(RankedCommand c) => Position(recent, c.Command.Id);

        if (string.IsNullOrWhiteSpace(query))
        {
            return [.. ranked.OrderBy(RecentIndex).ThenBy(c => Position(sectionsFirst, c.Command.Section))];
        }

        return
        [
            .. ranked
                .Select(c => (Command: c, Score: Match(c, query, RecentIndex(c) != int.MaxValue)))
                .Where(m => m.Score is not null)
                .OrderByDescending(m => m.Score)
                .ThenBy(m => m.Command.Title.Length)
                .Select(m => m.Command),
        ];
    }

    /// <summary>Its place in the list; what is not there goes after everything that is.</summary>
    private static int Position(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static int? Match(RankedCommand command, string query, bool isRecent)
    {
        var score = Score(command.Title, query);
        if (score is null && command.Command.Keys is { } keys && keys.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            score = query.Length * WordStartScore;
        }

        return score + (isRecent ? RecentBonus : 0);
    }
}
