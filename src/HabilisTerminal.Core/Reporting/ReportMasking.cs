using System.Text.RegularExpressions;

namespace HabilisTerminal.Core.Reporting;

/// <summary>
/// Hides who the user is before a report leaves the machine: the profile folder becomes <c>%USERPROFILE%</c>, the bare
/// user name <c>%USERNAME%</c> and the computer name <c>%COMPUTERNAME%</c>.
/// </summary>
public sealed partial class ReportMasking(string? userProfile, string? userName, string? machineName)
{
    /// <summary>The plugins this repository ships; any other plugin's id is the user's own business (issue #248).</summary>
    public static IReadOnlySet<string> PublicPluginIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "habi.archives", "aws.s3", "aws.sagemaker", "aws.sqs", "github", "gpu.monitor", "samples.demo", "samples.fileformat", "task.manager",
    };

    /// <summary>What a report says for a plugin: its id when it is one of ours, else "third-party plugin".</summary>
    public static string Plugin(string? id) => id is not null && PublicPluginIds.Contains(id) ? id : "third-party plugin";

    /// <summary>
    /// Crash and performance reports go to a public tracker (issue #248): a full path names the user's folders, projects
    /// and tickets. Each path keeps only its last part, "&lt;path&gt;\name", enough to tell a file from a folder.
    /// </summary>
    public static string HidePaths(string text) =>
        string.IsNullOrEmpty(text) ? text : PathPattern().Replace(text, match =>
        {
            var parts = match.Value.TrimEnd('\\').Split('\\');
            return parts.Length > 1 ? "<path>\\" + parts[^1] : "<path>";
        });

    // A drive, a UNC share or an environment variable such as %USERPROFILE%, then one or more parts. A part ends at a
    // backslash, a quote, a line end or a character no file name has.
    [GeneratedRegex(@"(?:(?<![\p{L}\p{N}])[A-Za-z]:|\\\\[^\\\s'""<>|*?]+|%[A-Za-z_][A-Za-z0-9_]*%)(?:\\[^\\\r\n'""<>|*?:`]+)+(?::line \d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

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
