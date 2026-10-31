using System.Text;

namespace HabilisTerminal.Core.Operations;

/// <summary>
/// Names Windows handles normally (SPEC §8): no reserved device names, no trailing dots or spaces, no invalid
/// characters — even though <c>\\?\</c> paths would allow creating them (and nothing could delete them afterwards).
/// </summary>
public static class NameRules
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    private static readonly char[] InvalidCharacters = Path.GetInvalidFileNameChars();

    /// <summary>Returns why the name is not allowed, or null when it is fine.</summary>
    public static string? Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "The name is empty.";
        }

        if (name is "." or "..")
        {
            return $"'{name}' is not a valid name.";
        }

        if (name.IndexOfAny(InvalidCharacters) >= 0)
        {
            return $"The name contains characters that are not allowed: {string.Join(" ", name.Where(c => InvalidCharacters.Contains(c)).Distinct().Select(Describe))}.";
        }

        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            return "The name must not end with a dot or a space.";
        }

        var stem = name.Split('.')[0].TrimEnd();
        if (Reserved.Contains(stem))
        {
            return $"'{stem}' is a reserved device name.";
        }

        return null;
    }

    /// <summary>Validates every component of a relative path such as <c>a\b\c</c>.</summary>
    public static string? ValidateRelativePath(string path)
    {
        foreach (var part in path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Validate(part) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>"report.txt" → "report (2).txt", then (3)… until <paramref name="exists"/> says the name is free.</summary>
    public static string NextFreeName(string name, Func<string, bool> exists)
    {
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (var number = 2; ; number++)
        {
            var candidate = $"{stem} ({number}){extension}";
            if (!exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static string Describe(char c) => c < 32 ? $"\\x{(int)c:X2}" : c.ToString();

    /// <summary>
    /// Far destination masks: <c>*.bak</c>, <c>new_*</c>, <c>*.*</c>, <c>?</c>. A mask with a dot maps name and
    /// extension separately; without a dot it applies to the whole name.
    /// </summary>
    public static string ApplyRenameMask(string name, string mask)
    {
        if (mask is "*" or "*.*")
        {
            return name;
        }

        if (!mask.Contains('.', StringComparison.Ordinal))
        {
            return ApplyPart(name, mask);
        }

        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[(dot + 1)..] : string.Empty;
        var maskDot = mask.LastIndexOf('.');
        var newStem = ApplyPart(stem, mask[..maskDot]);
        var newExtension = ApplyPart(extension, mask[(maskDot + 1)..]);
        return newExtension.Length == 0 ? newStem : $"{newStem}.{newExtension}";
    }

    private static string ApplyPart(string source, string mask)
    {
        var result = new StringBuilder();
        var position = 0;
        foreach (var c in mask)
        {
            switch (c)
            {
                case '*':
                    // The rest of the source part.
                    if (position < source.Length)
                    {
                        result.Append(source, position, source.Length - position);
                    }

                    position = source.Length;
                    break;
                case '?':
                    if (position < source.Length)
                    {
                        result.Append(source[position]);
                    }

                    position++;
                    break;
                default:
                    result.Append(c);
                    position++;
                    break;
            }
        }

        return result.ToString();
    }
}
