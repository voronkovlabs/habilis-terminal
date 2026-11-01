using System.Globalization;
using System.Text;

namespace HabilisTerminal.Core.Reporting;

/// <summary>A GitHub issue to create: title, Markdown body and labels.</summary>
public sealed record IssueDraft(string Title, string Body, IReadOnlyList<string> Labels);

/// <summary>Titles and bodies of crash and feedback issues, and the browser fallback link. Everything passes the masking.</summary>
public static class IssueText
{
    public const string CrashLabel = "crash";
    public const string FeedbackLabel = "feedback";
    private const int TitleLength = 80;

    /// <summary>GitHub and browsers handle links up to about 8 KB; longer bodies are cut for the browser fallback.</summary>
    private const int MaxUrlLength = 8000;

    /// <summary>Hidden in the body; a later crash with the same signature finds the issue by it.</summary>
    public static string SignatureMarker(string signature) => $"<!-- habi-crash-signature: {signature} -->";

    public static IssueDraft Crash(CrashReport report, ReportMasking masking)
    {
        var title = masking.Mask($"Crash: {ShortType(report.ExceptionType)}: {FirstLine(report.Message)}");
        var body = new StringBuilder()
            .AppendLine("Habilis Terminal crashed. This report was sent from the app after the user reviewed it.")
            .AppendLine()
            .Append(CrashDetails(report, masking))
            .AppendLine()
            .AppendLine(SignatureMarker(report.Signature))
            .ToString();
        return new IssueDraft(Cut(title, TitleLength), body, [CrashLabel]);
    }

    /// <summary>A comment for an open issue that already has this crash's signature.</summary>
    public static string CrashComment(CrashReport report, ReportMasking masking) =>
        "The same crash happened again.\n\n" + CrashDetails(report, masking);

    /// <summary>The text the user reviews before sending: the same body that goes to GitHub.</summary>
    public static string CrashPreview(CrashReport report, ReportMasking masking) => Crash(report, masking).Body;

    public static IssueDraft Feedback(string? title, string text, string version, string os, ReportMasking masking)
    {
        var trimmed = text.Trim();
        var heading = string.IsNullOrWhiteSpace(title) ? FirstLine(trimmed) : title.Trim();
        if (heading.Length == 0)
        {
            heading = "Feedback";
        }

        var body = new StringBuilder()
            .AppendLine(trimmed)
            .AppendLine()
            .AppendLine("---")
            .Append(CultureInfo.InvariantCulture, $"Habilis Terminal {version} · {os}")
            .AppendLine()
            .ToString();
        return new IssueDraft(Cut(masking.Mask(heading), TitleLength), masking.Mask(body), [FeedbackLabel]);
    }

    /// <summary>
    /// The prefilled "new issue" page, for builds without a token. Labels apply only for users who may label issues;
    /// a body too long for a link is cut with a note.
    /// </summary>
    public static string NewIssueUrl(string repositoryUrl, IssueDraft draft)
    {
        var start = $"{repositoryUrl.TrimEnd('/')}/issues/new?labels={Uri.EscapeDataString(string.Join(',', draft.Labels))}&title={Uri.EscapeDataString(draft.Title)}&body=";
        var body = draft.Body;
        var url = start + Uri.EscapeDataString(body);
        const string note = "\n\n(The rest did not fit in the link.)";
        while (url.Length > MaxUrlLength && body.Length > 0)
        {
            body = body[..(body.Length * 3 / 4)];
            url = start + Uri.EscapeDataString(body + note);
        }

        return url;
    }

    private static string CrashDetails(CrashReport report, ReportMasking masking)
    {
        var text = new StringBuilder()
            .AppendLine("| | |")
            .AppendLine("|---|---|")
            .AppendLine(CultureInfo.InvariantCulture, $"| Version | {report.Version} |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Windows | {report.Os} |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Time | {report.Time.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Caught by | {report.Source} |")
            .AppendLine(CultureInfo.InvariantCulture, $"| Signature | `{report.Signature}` |")
            .AppendLine()
            .AppendLine("### Exception")
            .AppendLine("````text")
            .AppendLine(report.Details.TrimEnd())
            .AppendLine("````");
        if (report.Trace.Count > 0)
        {
            text.AppendLine()
                .AppendLine("### Trace (last lines)")
                .AppendLine("````text")
                .AppendLine(string.Join('\n', report.Trace).TrimEnd())
                .AppendLine("````");
        }

        return masking.Mask(text.ToString());
    }

    private static string ShortType(string type) => type[(type.LastIndexOf('.') + 1)..];

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return line ?? string.Empty;
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..(length - 1)].TrimEnd() + "…";
}
