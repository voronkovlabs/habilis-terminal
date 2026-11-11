using System.Net;
using System.Text;
using HabilisTerminal.Plugins;

namespace Habilis.GitHub.Tests;

public sealed class GitHubTests : IDisposable
{
    private static readonly DateTime Day1 = new(2024, 1, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _folder = Directory.CreateTempSubdirectory("habi-tests-github-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData("gh:", null, null, null, "")]
    [InlineData("gh:me", "me", null, null, "")]
    [InlineData("gh:me/app", "me", "app", null, "")]
    [InlineData("gh:me/app/main/src/core", "me", "app", "main", "src/core")]
    [InlineData("gh:me/app/feature%2Fx%25y/docs", "me", "app", "feature/x%y", "docs")]
    public void Addresses_read_back_the_same_place(string address, string? owner, string? repo, string? branch, string path)
    {
        var location = GitHubLocation.Parse(address);
        Assert.Equal(new GitHubLocation(owner, repo, branch, path), location);
        Assert.Equal(address, location.Address);
    }

    [Fact]
    public void Going_up_steps_through_folders_branch_repo_owner()
    {
        var place = GitHubLocation.Parse("gh:me/app/feature%2Fx/src/core");
        Assert.Equal("gh:me/app/feature%2Fx/src", (place = place.Parent).Address);
        Assert.Equal("gh:me/app/feature%2Fx", (place = place.Parent).Address);
        Assert.Equal("gh:me/app", (place = place.Parent).Address);
        Assert.Equal("gh:me", (place = place.Parent).Address);
        Assert.True(place.Parent.IsTop);
    }

    [Fact]
    public async Task Owners_come_first_as_folders_of_their_repositories()
    {
        var api = new FakeApi();
        var panel = new GitHubPanel(new FakeHost(), api, GitHubLocation.Top, () => { });
        var owners = await Listing.AllAsync(panel);
        Assert.Equal(["acme", "me"], owners.Select(o => o.Name).Order());
        Assert.Equal(0, owners.Single(o => o.Name == "me").Group);
        Assert.Equal("4", owners.Single(o => o.Name == "me").Cells![GitHubPanel.ReposKey].Text);
        Assert.False(panel.ShowsParent);

        Assert.True(await panel.EnterAsync(owners.Single(o => o.Name == "me"), CancellationToken.None));
        var repos = await Listing.AllAsync(panel);
        var app = repos.Single(r => r.Name == "app");
        Assert.Equal(Day1, app.Cells![GitHubPanel.CreatedKey].SortValue);
        Assert.Equal(Day2, app.Modified);
        Assert.Equal("private", app.Cells[GitHubPanel.KindKey].Text);
        Assert.Equal(RowTone.Dim, repos.Single(r => r.Name == "old").Tone);
        Assert.Equal(1, api.RepositoryCalls);
    }

    [Fact]
    public async Task Branches_show_their_last_commit_at_once_and_when_they_began_soon_after()
    {
        var host = new FakeHost();
        var api = new FakeApi();
        var panel = new GitHubPanel(host, api, GitHubLocation.Parse("gh:me/app"), () => { });
        var branches = await Listing.AllAsync(panel);
        Assert.Equal("main", branches.Single(b => b.Group == 0).Name);
        Assert.Equal(Day1, branches.Single(b => b.Name == "main").Cells![GitHubPanel.CreatedKey].SortValue);
        Assert.Equal("…", branches.Single(b => b.Name == "feature/x").Cells![GitHubPanel.CreatedKey].Text);
        Assert.Equal(Day2, branches.Single(b => b.Name == "feature/x").Modified);

        // The background answers, then the panel asks for a refresh and lists again.
        for (var i = 0; i < 50 && host.Refreshed.Count == 0; i++)
        {
            await Task.Delay(50);
        }

        Assert.NotEmpty(host.Refreshed);
        branches = await Listing.AllAsync(panel);
        var feature = branches.Single(b => b.Name == "feature/x");
        Assert.Equal(Day1.AddDays(3), feature.Cells![GitHubPanel.CreatedKey].SortValue);
        Assert.Equal("2", feature.Cells[GitHubPanel.AheadKey].Text);
        Assert.Equal("—", branches.Single(b => b.Name == "empty").Cells![GitHubPanel.CreatedKey].Text);
        Assert.Equal(1, api.BranchCalls);
    }

    [Fact]
    public async Task A_branch_lists_its_files_and_folders_and_copies_them_out()
    {
        var api = new FakeApi();
        var panel = new GitHubPanel(new FakeHost(), api, GitHubLocation.Parse("gh:me/app/main"), () => { });
        var rows = await Listing.AllAsync(panel);
        Assert.Equal(["README.md", "src"], rows.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.Equal(5, rows.Single(r => r.Name == "README.md").Size);

        Assert.True(await panel.EnterAsync(rows.Single(r => r.Name == "src"), CancellationToken.None));
        Assert.Equal("gh:me/app/main/src", panel.Location);
        Assert.Equal(["a.cs", "deep"], (await Listing.AllAsync(panel)).Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.True(await panel.GoUpAsync(CancellationToken.None));

        var progress = new FakeProgress();
        var outcome = await panel.GetFilesAsync(new TransferRequest(rows, _folder, false, _ => Task.FromResult(OverwriteChoice.Overwrite)), progress, CancellationToken.None);
        Assert.Equal(3, outcome.Succeeded);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_folder, "README.md")));
        Assert.Equal("deep text", await File.ReadAllTextAsync(Path.Combine(_folder, "src", "deep", "b.txt")));
        Assert.Empty(Directory.GetFiles(_folder, "*.habi-part", SearchOption.AllDirectories));
        Assert.Equal(1, api.TreeCalls);
    }

    [Fact]
    public async Task A_truncated_tree_is_read_folder_by_folder()
    {
        var api = new FakeApi { Truncate = true };
        var panel = new GitHubPanel(new FakeHost(), api, GitHubLocation.Parse("gh:me/app/main/src/deep"), () => { });
        Assert.Equal(["b.txt"], (await Listing.AllAsync(panel)).Select(r => r.Name));
    }

    [Fact]
    public async Task A_name_that_leads_outside_the_target_is_not_written()
    {
        var api = new FakeApi();
        var panel = new GitHubPanel(new FakeHost(), api, GitHubLocation.Parse("gh:me/app/main"), () => { });
        await Listing.AllAsync(panel);
        var evil = new PanelEntry("..") { Tag = new TreeItem("..", false, 4, "sha-readme") };
        var outcome = await panel.GetFilesAsync(new TransferRequest([evil], _folder, false, _ => Task.FromResult(OverwriteChoice.Overwrite)), new FakeProgress(), CancellationToken.None);
        Assert.Equal(1, outcome.Failed);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_folder)!, "*.habi-part"));
    }

    [Fact]
    public async Task Files_get_their_last_commit_dates_in_the_background()
    {
        var host = new FakeHost();
        var panel = new GitHubPanel(host, new FakeApi(), GitHubLocation.Parse("gh:me/app/main"), () => { });
        Assert.All(await Listing.AllAsync(panel), r => Assert.Null(r.Modified));
        for (var i = 0; i < 50 && host.Refreshed.Count == 0; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(Day2, (await Listing.AllAsync(panel)).Single(r => r.Name == "README.md").Modified);
    }

    [Fact]
    public async Task Each_level_has_its_own_icons()
    {
        var panel = new GitHubPanel(new FakeHost(), new FakeApi(), GitHubLocation.Top, () => { });
        var owners = await Listing.AllAsync(panel);
        Assert.Equal(PanelIcons.User, owners.Single(o => o.Name == "me").Icon);
        Assert.Equal(PanelIcons.Organization, owners.Single(o => o.Name == "acme").Icon);

        await panel.EnterAsync(owners.Single(o => o.Name == "me"), CancellationToken.None);
        var repos = await Listing.AllAsync(panel);
        Assert.Equal(PanelIcons.PrivateRepository, repos.Single(r => r.Name == "app").Icon);
        Assert.Equal(PanelIcons.ArchivedRepository, repos.Single(r => r.Name == "old").Icon);
        Assert.Equal(PanelIcons.Repository, repos.Single(r => r.Name == "broken").Icon);

        await panel.EnterAsync(repos.Single(r => r.Name == "app"), CancellationToken.None);
        var branches = await Listing.AllAsync(panel);
        Assert.Equal(PanelIcons.DefaultBranch, branches.Single(b => b.Name == "main").Icon);
        Assert.Equal(PanelIcons.Branch, branches.Single(b => b.Name == "feature/x").Icon);
    }

    [Fact]
    public async Task An_empty_repository_lists_nothing_and_says_so()
    {
        var host = new FakeHost();
        var panel = new GitHubPanel(host, new FakeApi(), GitHubLocation.Parse("gh:me"), () => { });
        var repos = await Listing.AllAsync(panel);
        Assert.Equal("private, empty", repos.Single(r => r.Name == "empty-repo").Cells![GitHubPanel.KindKey].Text);

        Assert.True(await panel.EnterAsync(repos.Single(r => r.Name == "empty-repo"), CancellationToken.None));
        Assert.Empty(await Listing.AllAsync(panel));
        Assert.Contains(host.Messages, m => m.Message.Contains("is empty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_place_that_cannot_be_listed_leaves_the_panel_where_it_was()
    {
        var panel = new GitHubPanel(new FakeHost(), new FakeApi(), GitHubLocation.Parse("gh:me"), () => { });
        var repos = await Listing.AllAsync(panel);

        Assert.True(await panel.EnterAsync(repos.Single(r => r.Name == "broken"), CancellationToken.None));
        await Assert.ThrowsAsync<PluginException>(() => Listing.AllAsync(panel));
        Assert.Equal("gh:me", panel.Location);

        // The next Enter starts from the repositories, as the host still shows them.
        Assert.True(await panel.EnterAsync(repos.Single(r => r.Name == "app"), CancellationToken.None));
        Assert.Equal("gh:me/app", panel.Location);
        Assert.Contains("main", (await Listing.AllAsync(panel)).Select(b => b.Name));
    }

    [Fact]
    public void Sign_out_shows_once_in_the_command_line()
    {
        var panel = new GitHubPanel(new FakeHost(), new FakeApi(), GitHubLocation.Top, () => { });
        var signOut = panel.Commands.Single(c => c.Title == "Sign out of GitHub");
        Assert.False(signOut.AppliesTo!(null, []));
        Assert.Single(new GitHubPlugin().Commands, c => c.Title == "Sign out of GitHub");
    }

    [Fact]
    public async Task A_refused_token_signs_out()
    {
        var signedOut = false;
        var panel = new GitHubPanel(new FakeHost(), new FakeApi { Refuse = true }, GitHubLocation.Top, () => signedOut = true);
        await Assert.ThrowsAsync<SignInExpiredException>(() => Listing.AllAsync(panel));
        Assert.True(signedOut);
    }

    [Fact]
    public void Browser_addresses_follow_github()
    {
        var panel = new GitHubPanel(new FakeHost(), new FakeApi(), GitHubLocation.Parse("gh:me/app/feature%2Fx/src"), () => { });
        Assert.Equal("https://github.com/me/app/blob/feature/x/src/a.cs", panel.UrlFor(new PanelEntry("a.cs")));
        Assert.Equal("https://github.com/me/app/tree/feature/x/src", panel.UrlFor(null));
    }

    [Fact]
    public async Task Device_flow_waits_slows_down_and_gives_the_token()
    {
        var handler = new ScriptedHandler(
            """{"device_code":"dc","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""",
            """{"error":"authorization_pending"}""",
            """{"error":"slow_down","interval":10}""",
            """{"access_token":"tok","token_type":"bearer"}""");
        var waits = new List<TimeSpan>();
        var flow = new DeviceFlow(new HttpClient(handler), "client", (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        var code = await flow.StartAsync(CancellationToken.None);
        Assert.Equal("ABCD-1234", code.UserCode);
        Assert.Equal("tok", await flow.WaitForTokenAsync(code, CancellationToken.None));
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], waits);
        Assert.Contains("client_id=client", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("scope=repo+read%3Aorg", handler.Bodies[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"error":"access_denied"}""", "cancelled")]
    [InlineData("""{"error":"expired_token"}""", "expired")]
    public async Task Device_flow_says_why_it_stopped(string answer, string word)
    {
        var handler = new ScriptedHandler(
            """{"device_code":"dc","user_code":"X","verification_uri":"https://github.com/login/device","expires_in":900,"interval":1}""", answer);
        var flow = new DeviceFlow(new HttpClient(handler), "client", (_, _) => Task.CompletedTask);
        var code = await flow.StartAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<PluginException>(() => flow.WaitForTokenAsync(code, CancellationToken.None));
        Assert.Contains(word, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Device_flow_never_opens_a_page_that_is_not_github()
    {
        var handler = new ScriptedHandler("""{"device_code":"dc","user_code":"X","verification_uri":"https://evil.example/login","expires_in":900,"interval":1}""");
        var flow = new DeviceFlow(new HttpClient(handler), "client");
        await Assert.ThrowsAsync<PluginException>(() => flow.StartAsync(CancellationToken.None));
        Assert.False(DeviceFlow.IsGitHubPage(new Uri("https://github.com.evil.example/x")));
        Assert.False(DeviceFlow.IsGitHubPage(new Uri("http://github.com/login/device")));
        Assert.True(DeviceFlow.IsGitHubPage(new Uri("https://github.com/login/device")));
    }

    [Fact]
    public async Task The_api_reads_github_answers()
    {
        var handler = new ScriptedHandler(
            """{"data":{"viewer":{"login":"me","repositories":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"name":"app","owner":{"login":"me"},"isPrivate":true,"isFork":false,"isArchived":false,"createdAt":"2024-01-10T09:00:00Z","description":null,"url":"https://github.com/me/app","defaultBranchRef":{"name":"main","target":{"committedDate":"2025-06-01T12:00:00Z"}}}]}}}}""",
            """{"ahead_by":2,"behind_by":5,"commits":[{"commit":{"committer":{"date":"2024-01-13T09:00:00Z"}}}]}""",
            """{"sha":"t","truncated":false,"tree":[{"path":"a.txt","type":"blob","size":3,"sha":"s1"},{"path":"src","type":"tree","sha":"s2"},{"path":"lib","type":"commit","sha":"s3"}]}""");
        handler.Headers["x-ratelimit-remaining"] = "4999";
        handler.Headers["x-ratelimit-reset"] = "1767225600";
        var api = new GitHubApi(new HttpClient(handler), "secret-token");

        var (login, repos) = await api.RepositoriesAsync(CancellationToken.None);
        Assert.Equal("me", login);
        Assert.Equal(new RepoInfo("me", "app", true, false, false, Day1, Day2, "main", null, "https://github.com/me/app"), repos.Single());
        Assert.Equal("Bearer secret-token", handler.Authorizations[0]);

        var compare = await api.CompareAsync("me", "app", "main", "feature/x", CancellationToken.None);
        Assert.Equal(new CompareInfo(Day1.AddDays(3), 2, 5), compare);
        Assert.EndsWith("/compare/main...feature/x?per_page=1&page=1", handler.Uris[1], StringComparison.Ordinal);

        var tree = await api.TreeAsync("me", "app", "t", recursive: true, CancellationToken.None);
        Assert.Equal(["a.txt", "src"], tree.Items.Select(i => i.Path));
        Assert.Equal(4999, api.RateLimit!.Value.Remaining);
    }

    [Fact]
    public async Task The_api_tells_a_refused_token_and_a_used_up_limit()
    {
        var refused = new ScriptedHandler("{}") { Status = HttpStatusCode.Unauthorized };
        await Assert.ThrowsAsync<SignInExpiredException>(() => new GitHubApi(new HttpClient(refused), "t").RepositoriesAsync(CancellationToken.None));

        var limited = new ScriptedHandler("{}") { Status = HttpStatusCode.Forbidden };
        limited.Headers["x-ratelimit-remaining"] = "0";
        limited.Headers["x-ratelimit-reset"] = "1767225600";
        var error = await Assert.ThrowsAsync<PluginException>(() => new GitHubApi(new HttpClient(limited), "t").RepositoriesAsync(CancellationToken.None));
        Assert.Contains("limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_without_a_token_signs_in_with_the_built_in_client_id()
    {
        Assert.StartsWith("Ov23", GitHubPlugin.DefaultClientId, StringComparison.Ordinal);
        var handler = new ScriptedHandler(
            """{"device_code":"dc","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""");
        var host = new FakeHost();
        host.ChooseAnswers.Enqueue("With a code in the browser");
        host.ConfirmAnswers.Enqueue(false);
        var tokens = new MemoryTokens();
        var plugin = new GitHubPlugin { Tokens = tokens, Http = new HttpClient(handler) };
        plugin.Initialize(host);

        // The code is shown; declining the question opens nothing and keeps nothing.
        Assert.Null(await plugin.OpenAddressAsync("gh:", CancellationToken.None));
        Assert.Contains("ABCD-1234", host.Confirmations.Single().Message, StringComparison.Ordinal);
        Assert.Contains("client_id=" + GitHubPlugin.DefaultClientId, handler.Bodies[0], StringComparison.Ordinal);
        Assert.Null(tokens.Token);
    }

    [Fact]
    public async Task An_app_without_device_flow_says_what_to_tick()
    {
        var plugin = new GitHubPlugin { Tokens = new MemoryTokens(), Http = new HttpClient(new ScriptedHandler("""{"error":"device_flow_disabled"}""")) };
        var host = new FakeHost();
        host.ChooseAnswers.Enqueue("With a code in the browser");
        plugin.Initialize(host);
        var error = await Assert.ThrowsAsync<PluginException>(() => plugin.OpenAddressAsync("gh:", CancellationToken.None));
        Assert.Contains("Enable Device Flow", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stored_token_opens_the_panel_and_sign_out_forgets_it()
    {
        var tokens = new MemoryTokens { Token = "tok" };
        var host = new FakeHost();
        host.ConfirmAnswers.Enqueue(true);
        var plugin = new GitHubPlugin { Tokens = tokens, Connect = _ => new FakeApi() };
        plugin.Initialize(host);
        var panel = await plugin.OpenAddressAsync("gh:me/app", CancellationToken.None);
        Assert.Equal("gh:me/app", panel!.Location);

        await plugin.Commands.Single().Run(CancellationToken.None);
        Assert.Null(tokens.Token);
    }

    [Fact]
    public async Task The_github_cli_sign_in_is_used_as_it_is_and_never_kept()
    {
        var host = new FakeHost();
        host.ChooseAnswers.Enqueue("With the GitHub CLI's sign-in (gh)");
        var tokens = new MemoryTokens();
        var asked = 0;
        var used = new List<string>();
        var plugin = new GitHubPlugin
        {
            Tokens = tokens,
            GitHubCli = _ => { asked++; return Task.FromResult<string?>("gho_cli\n"); },
            Connect = token => { used.Add(token); return new FakeApi(); },
        };
        plugin.Initialize(host);

        Assert.NotNull(await plugin.OpenAddressAsync("gh:", CancellationToken.None));
        Assert.Null(tokens.Token);
        Assert.Equal("gh", host.Load()["signIn"]!.GetValue<string>());

        // The next time no question: the CLI is asked again, so its own sign-in changes follow.
        Assert.NotNull(await plugin.OpenAddressAsync("gh:me", CancellationToken.None));
        Assert.Equal(2, asked);
        Assert.All(used, t => Assert.Equal("gho_cli", t));

        host.ConfirmAnswers.Enqueue(true);
        await plugin.Commands.Single().Run(CancellationToken.None);
        Assert.Null(host.Load()["signIn"]);
    }

    [Fact]
    public async Task A_github_cli_that_is_not_signed_in_says_what_to_run()
    {
        var host = new FakeHost();
        host.ChooseAnswers.Enqueue("With the GitHub CLI's sign-in (gh)");
        var plugin = new GitHubPlugin { Tokens = new MemoryTokens(), GitHubCli = _ => Task.FromResult<string?>(null) };
        plugin.Initialize(host);
        var error = await Assert.ThrowsAsync<PluginException>(() => plugin.OpenAddressAsync("gh:", CancellationToken.None));
        Assert.Contains("gh auth login", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_personal_access_token_is_checked_and_kept()
    {
        var host = new FakeHost();
        host.ChooseAnswers.Enqueue("With a personal access token");
        host.ChooseAnswers.Enqueue("Open GitHub to create a token");
        host.InputAnswers.Enqueue("  ghp_pasted  ");
        var tokens = new MemoryTokens();
        var opened = new List<string>();
        var plugin = new GitHubPlugin { Tokens = tokens, Connect = _ => new FakeApi(), OpenBrowser = opened.Add };
        plugin.Initialize(host);

        Assert.NotNull(await plugin.OpenAddressAsync("gh:", CancellationToken.None));
        Assert.Equal("ghp_pasted", tokens.Token);

        // The page for a new token, with the scopes ticked.
        Assert.Equal([GitHubPlugin.NewTokenPage], opened);
        Assert.StartsWith("https://github.com/settings/tokens/new?scopes=repo,read:org", opened[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_github_refuses_is_not_kept()
    {
        var host = new FakeHost();
        host.ChooseAnswers.Enqueue("With a personal access token");
        host.ChooseAnswers.Enqueue("I already have a token");
        host.InputAnswers.Enqueue("ghp_wrong");
        var tokens = new MemoryTokens();
        var plugin = new GitHubPlugin { Tokens = tokens, Connect = _ => new FakeApi { Refuse = true } };
        plugin.Initialize(host);

        var error = await Assert.ThrowsAsync<PluginException>(() => plugin.OpenAddressAsync("gh:", CancellationToken.None));
        Assert.Contains("does not accept", error.Message, StringComparison.Ordinal);
        Assert.Null(tokens.Token);
    }

    [Fact]
    public async Task What_github_left_out_is_told()
    {
        var handler = new ScriptedHandler(
            """{"data":{"viewer":{"login":"me","repositories":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}},"errors":[{"type":"FORBIDDEN","message":"Resource protected by organization SAML enforcement."}]}""");
        var api = new GitHubApi(new HttpClient(handler), "t");
        var (login, repos) = await api.RepositoriesAsync(CancellationToken.None);
        Assert.Equal("me", login);
        Assert.Empty(repos);
        Assert.Contains("SAML", api.Warning, StringComparison.Ordinal);

        var host = new FakeHost();
        var panel = new GitHubPanel(host, new FakeApi { Warning = "GitHub left some out: SAML" }, GitHubLocation.Top, () => { });
        await Listing.AllAsync(panel);
        Assert.Contains(host.Messages, m => m.Message.Contains("SAML", StringComparison.Ordinal));
        Assert.Contains(panel.Info, l => l.Label == "Missing repositories?");
    }

    private sealed class MemoryTokens : ITokenStore
    {
        public string? Token { get; set; }

        public string? Read() => Token;

        public void Write(string login, string token) => Token = token;

        public void Delete() => Token = null;
    }

    /// <summary>me/app (private, main + feature/x + empty), me/old (archived), acme/site. The tree of main: README.md, src/a.cs, src/deep/b.txt.</summary>
    private sealed class FakeApi : IGitHubApi
    {
        private static readonly TreeItem[] Items =
        [
            new("README.md", false, 5, "sha-readme"),
            new("src", true, 0, "sha-src"),
            new("src/a.cs", false, 4, "sha-a"),
            new("src/deep", true, 0, "sha-deep"),
            new("src/deep/b.txt", false, 9, "sha-b"),
        ];

        private static readonly Dictionary<string, string> Blobs = new() { ["sha-readme"] = "hello", ["sha-a"] = "code", ["sha-b"] = "deep text" };

        public bool Truncate { get; init; }

        public bool Refuse { get; init; }

        public int RepositoryCalls { get; private set; }

        public int BranchCalls { get; private set; }

        public int TreeCalls { get; private set; }

        public (int Remaining, DateTime Reset)? RateLimit => (4000, Day2);

        public string? Warning { get; init; }

        public Task<(string Login, IReadOnlyList<RepoInfo> Repos)> RepositoriesAsync(CancellationToken cancellationToken)
        {
            if (Refuse)
            {
                throw new SignInExpiredException();
            }

            RepositoryCalls++;
            return Task.FromResult<(string, IReadOnlyList<RepoInfo>)>(("me",
            [
                new RepoInfo("me", "app", true, false, false, Day1, Day2, "main", null, ""),
                new RepoInfo("me", "old", false, false, true, Day1, Day1, "main", null, ""),
                new RepoInfo("acme", "site", false, true, false, Day1, null, "main", null, "") { OwnerIsOrganization = true },
                new RepoInfo("me", "empty-repo", true, false, false, Day1, null, null, null, ""),
                new RepoInfo("me", "broken", false, false, false, Day1, Day1, "main", null, ""),
            ]));
        }

        public Task<IReadOnlyList<BranchInfo>> BranchesAsync(string owner, string repo, CancellationToken cancellationToken)
        {
            BranchCalls++;
            if (repo == "empty-repo")
            {
                return Task.FromResult<IReadOnlyList<BranchInfo>>([]);
            }

            if (repo == "broken")
            {
                throw new PluginException("GitHub answered 500 Internal Server Error.");
            }

            return Task.FromResult<IReadOnlyList<BranchInfo>>(
            [
                new BranchInfo("main", Day2, "me", "c1", "tree-main"),
                new BranchInfo("feature/x", Day2, "me", "c2", "tree-x"),
                new BranchInfo("empty", Day1, "me", "c3", "tree-e"),
            ]);
        }

        public Task<CompareInfo> CompareAsync(string owner, string repo, string baseBranch, string branch, CancellationToken cancellationToken) =>
            Task.FromResult(branch == "feature/x" ? new CompareInfo(Day1.AddDays(3), 2, 1) : new CompareInfo(null, 0, 4));

        public Task<GitTree> TreeAsync(string owner, string repo, string treeSha, bool recursive, CancellationToken cancellationToken)
        {
            TreeCalls++;
            if (!Truncate)
            {
                return Task.FromResult(new GitTree(Items, false));
            }

            if (recursive)
            {
                return Task.FromResult(new GitTree(Items[..2], true));
            }

            // One level, with paths relative to the folder asked for.
            var folder = treeSha switch { "sha-src" => "src", "sha-deep" => "src/deep", _ => string.Empty };
            var prefix = folder.Length == 0 ? string.Empty : folder + "/";
            return Task.FromResult(new GitTree(
                [.. Items.Where(i => i.Path.StartsWith(prefix, StringComparison.Ordinal) && !i.Path[prefix.Length..].Contains('/')).Select(i => i with { Path = i.Path[prefix.Length..] })], false));
        }

        public Task<IReadOnlyDictionary<string, DateTime>> LastCommitsAsync(string owner, string repo, string branch, IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, DateTime>>(paths.ToDictionary(p => p, _ => Day2));

        public Task<Stream> BlobAsync(string owner, string repo, string sha, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(Blobs[sha])));
    }

    /// <summary>Answers requests with the given bodies in order, recording what was sent.</summary>
    private sealed class ScriptedHandler(params string[] bodies) : HttpMessageHandler
    {
        private int _next;

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public Dictionary<string, string> Headers { get; } = [];

        public List<string> Bodies { get; } = [];

        public List<string> Uris { get; } = [];

        public List<string?> Authorizations { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!.AbsoluteUri);
            Authorizations.Add(request.Headers.Authorization?.ToString());
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            var response = new HttpResponseMessage(Status) { Content = new StringContent(bodies[Math.Min(_next++, bodies.Length - 1)], Encoding.UTF8, "application/json") };
            foreach (var (name, value) in Headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        }
    }
}
