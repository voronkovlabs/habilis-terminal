using System.Net.Http.Headers;
using System.Net.Http.Json;
using HabilisTerminal.Core.Reporting;

namespace HabilisTerminal.Tools.ReportSmoke;

/// <summary>
/// Real-API check of the crash and feedback reports (issue #24), run by the manual report-smoke workflow with the
/// CRASH_REPORT_TOKEN secret: a new crash issue, the same crash again (must become a comment), a feedback issue. Every
/// created issue is marked as a test and closed again. The token comes from REPORT_SMOKE_TOKEN and is never printed.
/// </summary>
public static class Program
{
    private const string Marker = "TEST for #24";

    public static async Task<int> Main()
    {
        var token = Environment.GetEnvironmentVariable("REPORT_SMOKE_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            Console.Error.WriteLine("REPORT_SMOKE_TOKEN is not set.");
            return 2;
        }

        var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "voronkovlabs/habilis-terminal";
        using var http = new HttpClient();
        var client = new GitHubIssues(http, repository, token, "HabilisTerminal-ReportSmoke");
        var masking = ReportMasking.Current;
        var created = new List<int>();
        var failures = new List<string>();
        try
        {
            var report = Crash();
            var first = await client.ReportCrashAsync(report, masking);
            Console.WriteLine($"crash: {(first.IsComment ? "comment" : "issue")} #{first.Number} {first.Url} (signature {report.Signature})");
            if (!first.IsComment)
            {
                created.Add(first.Number);
            }
            else
            {
                failures.Add("the first crash became a comment; an open issue already had this signature");
            }

            // GitHub's label-filtered issue list lags a moment behind a new issue (run 41334798440 missed it after
            // about a second). A real repeat comes on a later app start; wait like one.
            await Task.Delay(TimeSpan.FromSeconds(20));
            var again = await client.ReportCrashAsync(report with { Time = DateTimeOffset.Now }, masking);
            Console.WriteLine($"same crash again: {(again.IsComment ? "comment" : "issue")} #{again.Number} {again.Url}");
            if (!again.IsComment || again.Number != first.Number)
            {
                failures.Add("the repeated crash did not become a comment on the first issue");
                if (!again.IsComment)
                {
                    created.Add(again.Number);
                }
            }

            var draft = IssueText.Feedback($"{Marker}: feedback smoke test",
                $"{Marker}. Feedback sent by the report-smoke workflow through the app's GitHub client.\nSecond line.",
                ReportEnvironment.VersionOf(typeof(IssueText).Assembly), ReportEnvironment.Windows(), masking);
            var feedback = await client.CreateAsync(draft);
            created.Add(feedback.Number);
            Console.WriteLine($"feedback: issue #{feedback.Number} {feedback.Url}");
        }
        catch (HttpRequestException ex)
        {
            failures.Add(ex.Message);
        }
        finally
        {
            foreach (var number in created)
            {
                await CloseAsync(http, client, repository, number);
            }
        }

        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"FAILED: {failure}");
        }

        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>A crash report from a real exception; the run id keeps the signature apart from earlier runs.</summary>
    private static CrashReport Crash()
    {
        var run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            throw new InvalidOperationException($"{Marker}: crash smoke test, run {run}");
        }
        catch (InvalidOperationException ex)
        {
            var report = CrashReport.FromException(ex, "report-smoke", ReportEnvironment.VersionOf(typeof(IssueText).Assembly), ReportEnvironment.Windows());
            return report with { Signature = CrashReport.ComputeSignature(report.ExceptionType + " " + run, ex.StackTrace) };
        }
    }

    private static async Task CloseAsync(HttpClient http, GitHubIssues client, string repository, int number)
    {
        try
        {
            await client.CommentAsync(number, "test send for #24");
            using var request = new HttpRequestMessage(HttpMethod.Patch, $"repos/{repository}/issues/{number}")
            {
                Content = JsonContent.Create(new { state = "closed", state_reason = "completed" }),
            };
            using var response = await http.SendAsync(request);
            Console.WriteLine($"closed #{number}: {(int)response.StatusCode}");
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"could not close #{number}: {ex.Message}");
        }
    }
}
