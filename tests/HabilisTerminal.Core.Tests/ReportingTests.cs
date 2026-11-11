using System.Net;
using System.Text.Json.Nodes;
using HabilisTerminal.Core.Reporting;

namespace HabilisTerminal.Core.Tests;

public class ReportingTests
{
    private static readonly ReportMasking Masking = new(@"C:\Users\Jane.Doe", "Jane.Doe", "JANE-PC");

    [Fact]
    public void Masking_hides_profile_paths_user_and_computer_names()
    {
        var text = Masking.Mask(@"Could not open c:\users\jane.doe\Documents\a.txt (C:/Users/Jane.Doe/x, ""C:\\Users\\Jane.Doe\\y"") by Jane.Doe on JANE-PC");

        Assert.Equal(@"Could not open %USERPROFILE%\Documents\a.txt (%USERPROFILE%/x, ""%USERPROFILE%\\y"") by %USERNAME% on %COMPUTERNAME%", text);
    }

    [Fact]
    public void Masking_replaces_whole_words_only()
    {
        var masking = new ReportMasking(@"C:\Users\al", "al", null);

        Assert.Equal("total: %USERNAME%, also", masking.Mask("total: al, also"));
    }

    [Fact]
    public void Signature_ignores_line_numbers_parameters_and_lambda_numbers()
    {
        const string first = """
               at HabilisTerminal.App.MainWindow.<Fire>b__12_0(Object sender) in D:\a\MainWindow.cs:line 10
               at HabilisTerminal.Core.Panels.PanelState.Move(Int32 delta) in D:\a\PanelState.cs:line 20
               at System.Windows.Threading.Dispatcher.Invoke()
            """;
        const string rebuilt = """
               at HabilisTerminal.App.MainWindow.<Fire>b__14_1(Object sender) in C:\b\MainWindow.cs:line 42
               at HabilisTerminal.Core.Panels.PanelState.Move(Int32 delta) in C:\b\PanelState.cs:line 21
               at System.Windows.Threading.Dispatcher.InvokeImpl()
            """;

        var signature = CrashReport.ComputeSignature("System.InvalidOperationException", first);

        Assert.Equal(12, signature.Length);
        Assert.Equal(signature, CrashReport.ComputeSignature("System.InvalidOperationException", rebuilt));
        Assert.NotEqual(signature, CrashReport.ComputeSignature("System.ArgumentException", first));
        Assert.NotEqual(signature, CrashReport.ComputeSignature("System.InvalidOperationException", first.Replace("Move", "Jump", StringComparison.Ordinal)));
    }

    [Fact]
    public void Report_uses_the_inner_exception_and_survives_a_round_trip()
    {
        using var sandbox = new TestSandbox();
        var report = CrashReport.FromException(Thrown(), "dispatcher", "0.1.1", "Windows 11", ["line 1"]);

        Assert.Equal("System.InvalidOperationException", report.ExceptionType);
        Assert.Equal("inner problem", report.Message);
        Assert.Contains("outer", report.Details, StringComparison.Ordinal);

        report.Save(sandbox.Path);
        var loaded = Assert.Single(CrashReport.LoadPending(sandbox.Path));
        Assert.Equal(report.Signature, loaded.Report.Signature);
        Assert.Equal(["line 1"], loaded.Report.Trace);
        Assert.Empty(CrashReport.LoadPending(Path.Combine(sandbox.Path, "missing")));
    }

    [Fact]
    public void Crash_issue_has_label_versions_masked_details_and_signature_marker()
    {
        var report = Report(@"Access to C:\Users\Jane.Doe\secret.txt is denied");

        var issue = IssueText.Crash(report, Masking);

        Assert.Equal(["crash"], issue.Labels);
        Assert.Equal(@"Crash: UnauthorizedAccessException: Access to <path>\secret.txt is denied", issue.Title); // paths hidden, #248
        Assert.Contains("| Version | 0.1.1 |", issue.Body, StringComparison.Ordinal);
        Assert.Contains("| Windows | Windows 11 24H2 |", issue.Body, StringComparison.Ordinal);
        Assert.Contains(IssueText.SignatureMarker("abc123abc123"), issue.Body, StringComparison.Ordinal);
        Assert.Contains("### Trace (last lines)", issue.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Jane", issue.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Feedback_title_comes_from_the_first_line_and_body_adds_versions()
    {
        var issue = IssueText.Feedback("  ", "\n  Tab should work like in Far  \nsecond line by Jane.Doe", "0.1.1", "Windows 11", Masking);

        Assert.Equal("Tab should work like in Far", issue.Title);
        Assert.Equal(["feedback"], issue.Labels);
        Assert.Contains("second line by %USERNAME%", issue.Body, StringComparison.Ordinal);
        Assert.EndsWith("Habilis Terminal 0.1.1 · Windows 11" + Environment.NewLine, issue.Body, StringComparison.Ordinal);
        Assert.Equal("Own title", IssueText.Feedback(" Own title ", "text", "0.1.1", "Windows 11", Masking).Title);
        Assert.Equal(80, IssueText.Feedback(null, new string('x', 200), "0.1.1", "Windows 11", Masking).Title.Length);
    }

    [Fact]
    public void New_issue_url_is_prefilled_and_fits_a_browser_link()
    {
        var url = IssueText.NewIssueUrl("https://github.com/voronkovlabs/habilis-terminal", new IssueDraft("A & B", new string('x', 20000), ["crash"]));

        Assert.StartsWith("https://github.com/voronkovlabs/habilis-terminal/issues/new?labels=crash&title=A%20%26%20B&body=xxx", url, StringComparison.Ordinal);
        Assert.True(url.Length <= 8000);
        Assert.EndsWith(Uri.EscapeDataString("(The rest did not fit in the link.)"), url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Crash_with_a_known_signature_becomes_a_comment()
    {
        var handler = new FakeGitHub(new JsonArray(
            new JsonObject { ["number"] = 5, ["body"] = "other", ["pull_request"] = null },
            new JsonObject { ["number"] = 6, ["body"] = "x " + IssueText.SignatureMarker("abc123abc123") + " y" }));
        using var http = new HttpClient(handler);

        var link = await new GitHubIssues(http, "o/r", "token", "test").ReportCrashAsync(Report("boom"), Masking, TestContext.Current.CancellationToken);

        Assert.True(link.IsComment);
        Assert.Equal(6, link.Number);
        Assert.Equal("POST repos/o/r/issues/6/comments", handler.Requests[^1].Line);
        Assert.Contains("happened again", handler.Requests[^1].Body, StringComparison.Ordinal);
        Assert.Equal("Bearer token", handler.Requests[0].Authorization);
        Assert.StartsWith("GET repos/o/r/issues?state=open&labels=crash", handler.Requests[0].Line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_crash_creates_a_labelled_issue_and_errors_carry_the_github_message()
    {
        var handler = new FakeGitHub([]);
        using var http = new HttpClient(handler);
        var client = new GitHubIssues(http, "o/r", "token", "test");

        var link = await client.ReportCrashAsync(Report("boom"), Masking, TestContext.Current.CancellationToken);

        Assert.False(link.IsComment);
        Assert.Equal("https://github.com/o/r/issues/7", link.Url);
        Assert.Equal("POST repos/o/r/issues", handler.Requests[^1].Line);
        var body = JsonNode.Parse(handler.Requests[^1].Body)!;
        Assert.Equal("crash", (string?)body["labels"]![0]);

        handler.Fail = true;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.CreateAsync(new IssueDraft("t", "b", ["feedback"]), TestContext.Current.CancellationToken));
        Assert.Contains("Bad credentials", error.Message, StringComparison.Ordinal);
    }

    private static CrashReport Report(string message) =>
        new(DateTimeOffset.UnixEpoch, "dispatcher", "0.1.1", "Windows 11 24H2", "System.UnauthorizedAccessException", message,
            $"System.UnauthorizedAccessException: {message}\n   at HabilisTerminal.App.X.Y() in C:\\Users\\Jane.Doe\\src\\X.cs:line 1",
            "abc123abc123", [@"key in C:\Users\Jane.Doe"]);

    private static System.Reflection.TargetInvocationException Thrown()
    {
        try
        {
            try
            {
                throw new InvalidOperationException("inner problem");
            }
            catch (InvalidOperationException inner)
            {
                throw new System.Reflection.TargetInvocationException("outer", inner);
            }
        }
        catch (System.Reflection.TargetInvocationException outer)
        {
            return outer;
        }
    }

    /// <summary>Answers like the GitHub REST API: the open issue list, then created issues and comments.</summary>
    private sealed class FakeGitHub(JsonArray openIssues) : HttpMessageHandler
    {
        public List<(string Line, string Body, string? Authorization)> Requests { get; } = [];

        public bool Fail { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            Requests.Add(($"{request.Method} {path}", body, request.Headers.Authorization?.ToString()));
            if (Fail)
            {
                return Json(HttpStatusCode.Unauthorized, new JsonObject { ["message"] = "Bad credentials" });
            }

            if (request.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.OK, openIssues);
            }

            return path.EndsWith("/comments", StringComparison.Ordinal)
                ? Json(HttpStatusCode.Created, new JsonObject { ["html_url"] = "https://github.com/o/r/issues/6#issuecomment-1" })
                : Json(HttpStatusCode.Created, new JsonObject { ["number"] = 7, ["html_url"] = "https://github.com/o/r/issues/7" });
        }

        private static HttpResponseMessage Json(HttpStatusCode status, JsonNode node) =>
            new(status) { Content = new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
    }
}
