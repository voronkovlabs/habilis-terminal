using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HabilisTerminal.Plugins;

namespace Habilis.GitHub;

internal sealed record RepoInfo(string Owner, string Name, bool IsPrivate, bool IsFork, bool IsArchived, DateTime Created, DateTime? LastCommit,
    string? DefaultBranch, string? Description, string Url);

/// <param name="TreeSha">The tree of the branch's last commit: its files.</param>
internal sealed record BranchInfo(string Name, DateTime? LastCommit, string? Author, string? CommitSha, string? TreeSha);

/// <param name="FirstOwnCommit">The oldest commit on the branch that is not on the base: when it began. Null without such commits.</param>
internal sealed record CompareInfo(DateTime? FirstOwnCommit, int Ahead, int Behind);

internal sealed record TreeItem(string Path, bool IsFolder, long Size, string Sha)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

internal sealed record GitTree(IReadOnlyList<TreeItem> Items, bool Truncated);

/// <summary>What the plugin asks GitHub; a fake stands in for it in tests.</summary>
internal interface IGitHubApi
{
    /// <summary>The signed-in user's login and every repository they own, work on or see through an organization.</summary>
    Task<(string Login, IReadOnlyList<RepoInfo> Repos)> RepositoriesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<BranchInfo>> BranchesAsync(string owner, string repo, CancellationToken cancellationToken);

    Task<CompareInfo> CompareAsync(string owner, string repo, string baseBranch, string branch, CancellationToken cancellationToken);

    Task<GitTree> TreeAsync(string owner, string repo, string treeSha, bool recursive, CancellationToken cancellationToken);

    /// <summary>The date of the last commit touching each path on the branch; paths with none are left out.</summary>
    Task<IReadOnlyDictionary<string, DateTime>> LastCommitsAsync(string owner, string repo, string branch, IReadOnlyList<string> paths, CancellationToken cancellationToken);

    /// <summary>A file's content.</summary>
    Task<Stream> BlobAsync(string owner, string repo, string sha, CancellationToken cancellationToken);

    /// <summary>Requests left this hour and when the count resets, from the last answer; null before any.</summary>
    (int Remaining, DateTime Reset)? RateLimit { get; }
}

/// <summary>The token was refused: it was revoked or expired. The plugin forgets it and asks to sign in again.</summary>
internal sealed class SignInExpiredException() : PluginException("GitHub no longer accepts the sign-in (the token was revoked or expired). Open GitHub again to sign in.");

/// <summary>GitHub's REST and GraphQL API over HTTPS, straight from the plugin.</summary>
internal sealed class GitHubApi(HttpClient http, string token) : IGitHubApi
{
    private static readonly Uri Rest = new("https://api.github.com/");
    private static readonly Uri GraphQl = new("https://api.github.com/graphql");

    /// <summary>GraphQL fields per request for file dates: GitHub limits a query's cost.</summary>
    public const int PathsPerQuery = 40;

    public (int Remaining, DateTime Reset)? RateLimit { get; private set; }

    public async Task<(string Login, IReadOnlyList<RepoInfo> Repos)> RepositoriesAsync(CancellationToken cancellationToken)
    {
        const string Query = """
            query($after: String) {
              viewer {
                login
                repositories(first: 100, after: $after, ownerAffiliations: [OWNER, COLLABORATOR, ORGANIZATION_MEMBER], orderBy: {field: NAME, direction: ASC}) {
                  pageInfo { hasNextPage endCursor }
                  nodes {
                    name owner { login } isPrivate isFork isArchived createdAt description url
                    defaultBranchRef { name target { ... on Commit { committedDate } } }
                  }
                }
              }
            }
            """;
        var repos = new List<RepoInfo>();
        var login = string.Empty;
        string? after = null;
        do
        {
            using var json = await GraphAsync(Query, new Dictionary<string, object?> { ["after"] = after }, cancellationToken).ConfigureAwait(false);
            var viewer = json.RootElement.GetProperty("data").GetProperty("viewer");
            login = viewer.GetProperty("login").GetString() ?? string.Empty;
            var connection = viewer.GetProperty("repositories");
            foreach (var node in connection.GetProperty("nodes").EnumerateArray())
            {
                var branch = node.GetProperty("defaultBranchRef");
                repos.Add(new RepoInfo(
                    node.GetProperty("owner").GetProperty("login").GetString()!,
                    node.GetProperty("name").GetString()!,
                    node.GetProperty("isPrivate").GetBoolean(),
                    node.GetProperty("isFork").GetBoolean(),
                    node.GetProperty("isArchived").GetBoolean(),
                    Date(node, "createdAt") ?? DateTime.MinValue,
                    branch.ValueKind == JsonValueKind.Object ? Date(branch.GetProperty("target"), "committedDate") : null,
                    branch.ValueKind == JsonValueKind.Object ? branch.GetProperty("name").GetString() : null,
                    Text(node, "description"),
                    Text(node, "url") ?? string.Empty));
            }

            after = NextPage(connection);
        }
        while (after is not null);

        return (login, repos);
    }

    public async Task<IReadOnlyList<BranchInfo>> BranchesAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        const string Query = """
            query($owner: String!, $name: String!, $after: String) {
              repository(owner: $owner, name: $name) {
                refs(refPrefix: "refs/heads/", first: 100, after: $after) {
                  pageInfo { hasNextPage endCursor }
                  nodes { name target { ... on Commit { oid committedDate author { name } tree { oid } } } }
                }
              }
            }
            """;
        var branches = new List<BranchInfo>();
        string? after = null;
        do
        {
            using var json = await GraphAsync(Query, new Dictionary<string, object?> { ["owner"] = owner, ["name"] = repo, ["after"] = after }, cancellationToken).ConfigureAwait(false);
            var repository = json.RootElement.GetProperty("data").GetProperty("repository");
            if (repository.ValueKind != JsonValueKind.Object)
            {
                throw new PluginException($"{owner}/{repo} is not there, or you cannot see it.");
            }

            var refs = repository.GetProperty("refs");
            foreach (var node in refs.GetProperty("nodes").EnumerateArray())
            {
                var target = node.GetProperty("target");
                var hasCommit = target.ValueKind == JsonValueKind.Object && target.TryGetProperty("oid", out _);
                branches.Add(new BranchInfo(
                    node.GetProperty("name").GetString()!,
                    hasCommit ? Date(target, "committedDate") : null,
                    hasCommit && target.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object ? Text(author, "name") : null,
                    hasCommit ? Text(target, "oid") : null,
                    hasCommit ? Text(target.GetProperty("tree"), "oid") : null));
            }

            after = NextPage(refs);
        }
        while (after is not null);

        return branches;
    }

    public async Task<CompareInfo> CompareAsync(string owner, string repo, string baseBranch, string branch, CancellationToken cancellationToken)
    {
        // The oldest commit comes first, so one per page is the first one the branch has of its own.
        using var json = await RestAsync($"repos/{Segment(owner)}/{Segment(repo)}/compare/{Ref(baseBranch)}...{Ref(branch)}?per_page=1&page=1", cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        DateTime? first = null;
        if (root.TryGetProperty("commits", out var commits) && commits.GetArrayLength() > 0)
        {
            var commit = commits[0].GetProperty("commit");
            first = Date(commit.GetProperty("committer"), "date");
        }

        return new CompareInfo(first, root.GetProperty("ahead_by").GetInt32(), root.GetProperty("behind_by").GetInt32());
    }

    public async Task<GitTree> TreeAsync(string owner, string repo, string treeSha, bool recursive, CancellationToken cancellationToken)
    {
        using var json = await RestAsync($"repos/{Segment(owner)}/{Segment(repo)}/git/trees/{Segment(treeSha)}{(recursive ? "?recursive=1" : string.Empty)}", cancellationToken).ConfigureAwait(false);
        var items = new List<TreeItem>();
        foreach (var node in json.RootElement.GetProperty("tree").EnumerateArray())
        {
            var type = Text(node, "type");
            if (type is not ("blob" or "tree"))
            {
                continue; // submodules ("commit") have no content here
            }

            items.Add(new TreeItem(Text(node, "path")!, type == "tree", node.TryGetProperty("size", out var size) ? size.GetInt64() : 0, Text(node, "sha")!));
        }

        return new GitTree(items, json.RootElement.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean());
    }

    public async Task<IReadOnlyDictionary<string, DateTime>> LastCommitsAsync(string owner, string repo, string branch, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        for (var start = 0; start < paths.Count; start += PathsPerQuery)
        {
            var batch = paths.Skip(start).Take(PathsPerQuery).ToList();
            var fields = new StringBuilder();
            for (var i = 0; i < batch.Count; i++)
            {
                fields.Append(CultureInfo.InvariantCulture, $"p{i}: history(first: 1, path: {JsonSerializer.Serialize(batch[i])}) {{ nodes {{ committedDate }} }}\n");
            }

            var query = $$"""
                query($owner: String!, $name: String!, $ref: String!) {
                  repository(owner: $owner, name: $name) { ref(qualifiedName: $ref) { target { ... on Commit { {{fields}} } } } }
                }
                """;
            using var json = await GraphAsync(query, new Dictionary<string, object?> { ["owner"] = owner, ["name"] = repo, ["ref"] = "refs/heads/" + branch }, cancellationToken).ConfigureAwait(false);
            if (!json.RootElement.GetProperty("data").TryGetProperty("repository", out var repository) || repository.ValueKind != JsonValueKind.Object
                || repository.GetProperty("ref") is not { ValueKind: JsonValueKind.Object } reference)
            {
                continue;
            }

            var target = reference.GetProperty("target");
            for (var i = 0; i < batch.Count; i++)
            {
                if (target.TryGetProperty($"p{i}", out var history) && history.GetProperty("nodes") is { } nodes && nodes.GetArrayLength() > 0
                    && Date(nodes[0], "committedDate") is { } date)
                {
                    result[batch[i]] = date;
                }
            }
        }

        return result;
    }

    public async Task<Stream> BlobAsync(string owner, string repo, string sha, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Rest, $"repos/{Segment(owner)}/{Segment(repo)}/git/blobs/{Segment(sha)}"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
        var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return new ResponseStream(response, await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<JsonDocument> RestAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Rest, path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> GraphAsync(string query, Dictionary<string, object?> variables, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQl)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query, variables }), Encoding.UTF8, "application/json"),
        };
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (json.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0
            && (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object))
        {
            var message = Text(errors[0], "message") ?? "an error";
            json.Dispose();
            throw new PluginException($"GitHub: {message}");
        }

        return json;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("HabilisTerminal-GitHub/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new PluginException($"Cannot reach GitHub: {ex.Message}", ex);
        }

        ReadRateLimit(response);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new SignInExpiredException(),
                HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests when RateLimit is { Remaining: 0 } limit =>
                    new PluginException($"GitHub's limit of requests for this hour is used up; it resets at {limit.Reset.ToLocalTime():t}."),
                HttpStatusCode.NotFound => new PluginException("GitHub: not found, or you cannot see it."),
                HttpStatusCode.Conflict => new PluginException("GitHub: the repository is empty."),
                _ => new PluginException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}."),
            };
        }
    }

    private void ReadRateLimit(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && int.TryParse(remaining.FirstOrDefault(), CultureInfo.InvariantCulture, out var left)
            && response.Headers.TryGetValues("x-ratelimit-reset", out var reset) && long.TryParse(reset.FirstOrDefault(), CultureInfo.InvariantCulture, out var seconds))
        {
            RateLimit = (left, DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new PluginException("GitHub's answer did not look right; try again.", ex);
        }
    }

    private static string? NextPage(JsonElement connection)
    {
        var page = connection.GetProperty("pageInfo");
        return page.GetProperty("hasNextPage").GetBoolean() ? Text(page, "endCursor") : null;
    }

    private static string Segment(string text) => Uri.EscapeDataString(text);

    /// <summary>A branch in a path: escaped, but its "/" kept, as GitHub reads branch names with slashes there.</summary>
    private static string Ref(string branch) => Uri.EscapeDataString(branch).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTime? Date(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? DateTime.SpecifyKind(date, DateTimeKind.Utc)
            : null;

    /// <summary>A response body that disposes its response when closed.</summary>
    private sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
