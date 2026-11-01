using System.Net.Http;
using System.Reflection;
using System.Text;
using HabilisTerminal.App.Updates;
using HabilisTerminal.Core.Reporting;

namespace HabilisTerminal.App.Reporting;

/// <summary>How a send ended: a created issue or comment, the browser page opened instead, or an error to show.</summary>
public sealed record SendResult(IssueLink? Link, bool OpenedInBrowser, string? Error);

/// <summary>
/// Sends crash reports and feedback to GitHub issues. Release builds carry a token (<c>CRASH_REPORT_TOKEN</c>, injected
/// by the release workflow, never committed); builds without one open the prefilled new-issue page in the browser.
/// </summary>
public static class ReportSender
{
    private const string Repository = "voronkovlabs/habilis-terminal";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static string Version { get; } = ReportEnvironment.VersionOf(typeof(ReportSender).Assembly);

    public static bool HasToken => Token is not null;

    /// <summary>
    /// <c>HABI_REPORT_TOKEN</c> overrides it for a deliberate test send from a development build. The embedded value is
    /// base64 of the reversed token, so the binary holds no plain <c>github_pat_</c> string.
    /// </summary>
    private static string? Token { get; } = ReadToken();

    public static Task<SendResult> SendCrashAsync(CrashReport report) =>
        SendAsync(client => client.ReportCrashAsync(report, ReportMasking.Current), IssueText.Crash(report, ReportMasking.Current));

    public static Task<SendResult> SendFeedbackAsync(IssueDraft draft) => SendAsync(client => client.CreateAsync(draft), draft);

    /// <summary>The browser fallback, also offered after a failed send.</summary>
    public static void OpenInBrowser(IssueDraft draft) => App.OpenInBrowser(IssueText.NewIssueUrl(UpdateService.RepositoryUrl, draft));

    private static async Task<SendResult> SendAsync(Func<GitHubIssues, Task<IssueLink>> send, IssueDraft fallback)
    {
        if (Token is not { } token)
        {
            OpenInBrowser(fallback);
            return new SendResult(null, OpenedInBrowser: true, null);
        }

        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            var link = await send(new GitHubIssues(http, Repository, token, $"HabilisTerminal/{Version}"));
            return new SendResult(link, OpenedInBrowser: false, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException)
        {
            return new SendResult(null, OpenedInBrowser: false, ex is TaskCanceledException ? "GitHub did not answer in time." : ex.Message);
        }
    }

    private static string? ReadToken()
    {
        if (Environment.GetEnvironmentVariable("HABI_REPORT_TOKEN") is { Length: > 0 } overridden)
        {
            return overridden;
        }

        var encoded = typeof(ReportSender).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "CrashReportToken")?.Value;
        if (string.IsNullOrEmpty(encoded))
        {
            return null;
        }

        try
        {
            var reversed = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return new string([.. reversed.Reverse()]);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
