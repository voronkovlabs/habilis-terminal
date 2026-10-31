using System.Text;
using System.Text.RegularExpressions;

namespace HabilisTerminal.Core.FileSystem;

/// <summary>
/// Far Manager file masks: <c>*</c>, <c>?</c>, <c>[a-z]</c> sets, several masks separated by <c>,</c> or <c>;</c>,
/// quoted masks with separators inside, exclusions after <c>|</c> (<c>*.cs|*.g.cs</c>), and regular expressions
/// <c>/pattern/flags</c>. Matching is case-insensitive. <c>*.*</c> matches every name; <c>*.</c> matches names
/// without an extension.
/// </summary>
public sealed class FileMask
{
    private readonly Regex[] _include;
    private readonly Regex[] _exclude;

    private FileMask(string text, Regex[] include, Regex[] exclude)
    {
        Text = text;
        _include = include;
        _exclude = exclude;
    }

    public static FileMask All { get; } = Parse("*");

    public string Text { get; }

    /// <exception cref="FormatException">Empty mask, unbalanced quotes or brackets, or an invalid regular expression.</exception>
    public static FileMask Parse(string text)
    {
        var bar = IndexOutsideQuotes(text, '|');
        var includeText = bar < 0 ? text : text[..bar];
        var excludeText = bar < 0 ? string.Empty : text[(bar + 1)..];

        var include = SplitMasks(includeText).Select(Compile).ToArray();
        if (include.Length == 0)
        {
            // "|*.tmp" means "everything except *.tmp".
            include = bar >= 0 ? [Compile("*")] : throw new FormatException("The mask is empty.");
        }

        var exclude = SplitMasks(excludeText).Select(Compile).ToArray();
        return new FileMask(text, include, exclude);
    }

    public static bool TryParse(string text, out FileMask? mask)
    {
        try
        {
            mask = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            mask = null;
            return false;
        }
    }

    public bool IsMatch(string name) =>
        _include.Any(r => r.IsMatch(name)) && !_exclude.Any(r => r.IsMatch(name));

    private static int IndexOutsideQuotes(string text, char target)
    {
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                quoted = !quoted;
            }
            else if (text[i] == target && !quoted)
            {
                return i;
            }
        }

        return -1;
    }

    private static IEnumerable<string> SplitMasks(string text)
    {
        var current = new StringBuilder();
        var quoted = false;
        var inRegex = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && !inRegex)
            {
                quoted = !quoted;
                continue;
            }

            if (c == '/' && !quoted && current.Length == 0)
            {
                inRegex = true;
            }
            else if (c == '/' && inRegex && i > 0 && text[i - 1] != '\\')
            {
                // Closing slash; flags may follow until the next separator.
                inRegex = false;
            }

            if ((c == ',' || c == ';') && !quoted && !inRegex)
            {
                if (current.ToString().Trim() is { Length: > 0 } mask)
                {
                    yield return mask;
                }

                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (quoted)
        {
            throw new FormatException("Unbalanced quotes in the mask.");
        }

        if (current.ToString().Trim() is { Length: > 0 } last)
        {
            yield return last;
        }
    }

    private static Regex Compile(string mask)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        if (mask.Length >= 2 && mask[0] == '/' && mask.LastIndexOf('/') > 0)
        {
            var end = mask.LastIndexOf('/');
            var flags = mask[(end + 1)..];
            var regexOptions = RegexOptions.CultureInvariant;
            foreach (var flag in flags)
            {
                regexOptions |= flag switch
                {
                    'i' => RegexOptions.IgnoreCase,
                    'm' => RegexOptions.Multiline,
                    's' => RegexOptions.Singleline,
                    'x' => RegexOptions.IgnorePatternWhitespace,
                    _ => throw new FormatException($"Unknown regular expression flag '{flag}'."),
                };
            }

            try
            {
                return new Regex(mask[1..end], regexOptions);
            }
            catch (ArgumentException ex)
            {
                throw new FormatException($"Invalid regular expression: {ex.Message}", ex);
            }
        }

        if (mask == "*.*")
        {
            return new Regex("^.*$", options);
        }

        var pattern = new StringBuilder("^");
        for (var i = 0; i < mask.Length; i++)
        {
            var c = mask[i];
            switch (c)
            {
                case '*':
                    pattern.Append(".*");
                    break;
                case '?':
                    pattern.Append('.');
                    break;
                case '[':
                    var close = mask.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        throw new FormatException("Unbalanced '[' in the mask.");
                    }

                    pattern.Append('[').Append(EscapeSet(mask[(i + 1)..close])).Append(']');
                    i = close;
                    break;
                case '.' when i == mask.Length - 1:
                    // Far: a trailing dot means "no extension".
                    pattern.Append("(?<!\\..*)");
                    break;
                default:
                    pattern.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return new Regex(pattern.Append('$').ToString(), options);
    }

    private static string EscapeSet(string set)
    {
        var result = new StringBuilder();
        foreach (var c in set)
        {
            result.Append(c is '\\' or '^' or '[' or ']' ? "\\" + c : c.ToString());
        }

        return result.ToString();
    }
}
