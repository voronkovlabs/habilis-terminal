using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace HabilisTerminal.Core.Reporting;

/// <summary>An issue or comment on GitHub: the issue number and the page to open.</summary>
public sealed record IssueLink(int Number, string Url, bool IsComment);

/// <summary>
/// Creates crash and feedback issues through the GitHub REST API with a token that may only touch this repository's
/// issues. A crash whose signature is already in an open <c>crash</c> issue becomes a comment there instead.
/// </summary>
public sealed class GitHubIssues
{
    private const int MaxPages = 3;
    private readonly HttpClient _http;
    private readonly string _repository;

    /// <param name="repository"><c>owner/name</c>.</param>
    public GitHubIssues(HttpClient http, string repository, string token, string userAgent)
    {
        _http = http;
        _repository = repository;
        _http.BaseAddress ??= new Uri("https://api.github.com/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<IssueLink> ReportCrashAsync(CrashReport report, ReportMasking masking, CancellationToken cancellation = default)
    {
        if (await FindOpenAsync(IssueText.CrashLabel, IssueText.SignatureMarker(report.Signature), cancellation).ConfigureAwait(false) is { } existing)
        {
            return await CommentAsync(existing, IssueText.CrashComment(report, masking), cancellation).ConfigureAwait(false);
        }

        return await CreateAsync(IssueText.Crash(report, masking), cancellation).ConfigureAwait(false);
    }

    public async Task<IssueLink> CreateAsync(IssueDraft draft, CancellationToken cancellation = default)
    {
        using var response = await _http.PostAsJsonAsync($"repos/{_repository}/issues",
            new { title = draft.Title, body = draft.Body, labels = draft.Labels }, cancellation).ConfigureAwait(false);
        var json = await ReadAsync(response, cancellation).ConfigureAwait(false);
        return new IssueLink((int)json["number"]!, (string)json["html_url"]!, IsComment: false);
    }

    public async Task<IssueLink> CommentAsync(int issue, string body, CancellationToken cancellation = default)
    {
        using var response = await _http.PostAsJsonAsync($"repos/{_repository}/issues/{issue}/comments", new { body }, cancellation).ConfigureAwait(false);
        var json = await ReadAsync(response, cancellation).ConfigureAwait(false);
        return new IssueLink(issue, (string)json["html_url"]!, IsComment: true);
    }

    /// <summary>The open issue with <paramref name="label"/> whose body contains <paramref name="marker"/>, or null.</summary>
    public async Task<int?> FindOpenAsync(string label, string marker, CancellationToken cancellation = default)
    {
        for (var page = 1; page <= MaxPages; page++)
        {
            using var response = await _http.GetAsync($"repos/{_repository}/issues?state=open&labels={Uri.EscapeDataString(label)}&per_page=100&page={page}", cancellation).ConfigureAwait(false);
            if (await ReadAsync(response, cancellation).ConfigureAwait(false) is not JsonArray issues)
            {
                return null;
            }

            foreach (var issue in issues.OfType<JsonObject>())
            {
                if (issue["pull_request"] is null && ((string?)issue["body"])?.Contains(marker, StringComparison.Ordinal) == true)
                {
                    return (int)issue["number"]!;
                }
            }

            if (issues.Count < 100)
            {
                return null;
            }
        }

        return null;
    }

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var message = TryParse(text)?["message"]?.GetValue<string>() ?? response.ReasonPhrase;
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode}: {message}", null, response.StatusCode);
        }

        return TryParse(text) ?? throw new HttpRequestException("GitHub sent an unreadable answer.");
    }

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
