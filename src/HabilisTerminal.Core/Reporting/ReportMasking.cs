using System.Text.RegularExpressions;

namespace HabilisTerminal.Core.Reporting;

/// <summary>
/// Hides who the user is before a report leaves the machine: the profile folder becomes <c>%USERPROFILE%</c>, the bare
/// user name <c>%USERNAME%</c> and the computer name <c>%COMPUTERNAME%</c>.
/// </summary>
public sealed class ReportMasking(string? userProfile, string? userName, string? machineName)
{
    public static ReportMasking Current { get; } = new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName);

    public string Mask(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (!string.IsNullOrEmpty(userProfile))
        {
            var profile = userProfile.TrimEnd('\\', '/');
            // As written in paths, in forward-slash paths and in JSON / C# escaped strings.
            foreach (var variant in new[] { profile, profile.Replace('\\', '/'), profile.Replace("\\", "\\\\", StringComparison.Ordinal) })
            {
                text = text.Replace(variant, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            }
        }

        text = ReplaceWord(text, userName, "%USERNAME%");
        return ReplaceWord(text, machineName, "%COMPUTERNAME%");
    }

    /// <summary>Whole words only, so a short name does not eat parts of other words.</summary>
    private static string ReplaceWord(string text, string? word, string replacement) =>
        string.IsNullOrWhiteSpace(word) ? text
            : Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(word)}(?![\p{{L}}\p{{N}}_])", replacement.Replace("$", "$$", StringComparison.Ordinal), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
