using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using HabilisTerminal.Plugins;

namespace Habilis.GitHub;

/// <summary>
/// GitHub as a read-only file panel (issue #214): owners, their repositories, a repository's branches, a branch's
/// folders and files. Dates that cost a request per row (when a branch began, when a file last changed) are filled in
/// the background after the listing shows, and the panel refreshes as they come.
/// </summary>
internal sealed class GitHubPanel : IPanelProvider, IPanelCommands, IGetFiles
{
    public const string CreatedKey = "created";
    public const string KindKey = "kind";
    public const string ReposKey = "repos";
    public const string AheadKey = "ahead";
    public const string BehindKey = "behind";
    public const string AuthorKey = "author";

    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMilliseconds(400);

    private readonly IPluginHost _host;
    private readonly IGitHubApi _api;
    private readonly Action _signedOut;

    // What GitHub said, kept until Refresh: going in and out of places costs nothing.
    private (string Login, IReadOnlyList<RepoInfo> Repos)? _repos;
    private readonly ConcurrentDictionary<string, IReadOnlyList<BranchInfo>> _branches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CompareInfo> _compares = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GitTree> _trees = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<TreeItem>> _folders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime?> _fileDates = new(StringComparer.Ordinal);

    private GitHubLocation _location;
    private GitHubLocation? _listed;
    private CancellationTokenSource _background = new();
    private int _refreshQueued;
    private bool _quietRefresh;
    private int _rowCount;

    public GitHubPanel(IPluginHost host, IGitHubApi api, GitHubLocation location, Action signedOut)
    {
        _host = host;
        _api = api;
        _location = location;
        _signedOut = signedOut;
        Commands =
        [
            new PanelCommand("Open in browser", null, OpenInBrowserAsync),
            new PanelCommand("Clone address", null, ShowCloneAddressAsync) { AppliesTo = (current, _) => !_location.IsTop && (!_location.IsOwner || current is not null) },
            new PanelCommand("Refresh from GitHub", null, RefreshAsync),
            new PanelCommand("Sign out of GitHub", null, SignOutAsync),
        ];
    }

    public string Title => _location.IsTop ? "GitHub" : "GitHub · " + string.Join(" · ", new[] { _location.Owner, _location.Repo, _location.Branch }.Where(p => p is not null));

    public string Location => _location.Address;

    internal GitHubLocation Current => _location;

    public IReadOnlyList<PanelLayout> Layouts => _location switch
    {
        { IsTop: true } => [new("Owners", [new PanelColumn(ReposKey, "Repositories", 100, AlignRight: true)])],
        { IsOwner: true } => [new("Repositories", [Created, LastCommit, new PanelColumn(KindKey, "Kind", 90)]), new("Names", [])],
        { IsRepo: true } =>
        [
            new("Branches", [Created, LastCommit, new PanelColumn(AheadKey, "Ahead", 60, AlignRight: true), new PanelColumn(BehindKey, "Behind", 60, AlignRight: true)]),
            new("Authors", [LastCommit, new PanelColumn(AuthorKey, "Last author", 160)]),
        ],
        _ => [new("Files", [PanelColumn.Size, LastCommit]), new("Names", [])],
    };

    public IReadOnlyList<PanelSortKey> SortKeys { get; } =
        [new(CreatedKey, "Created"), new(ReposKey, "Repositories"), new(AheadKey, "Ahead"), new(BehindKey, "Behind"), new(AuthorKey, "Last author")];

    public IReadOnlyList<PanelCommand> Commands { get; }

    public bool CanMove => false;

    /// <summary>The owners are the top: no "..", Ctrl+PgUp leaves the panel.</summary>
    public bool ShowsParent => !_location.IsTop;

    public IReadOnlyList<InfoLine> Info
    {
        get
        {
            var lines = new List<InfoLine>();
            if (_repos is { } repos)
            {
                lines.Add(new("Signed in as", repos.Login));
            }

            lines.Add(new("Location", _location.Address));
            lines.Add(new("Rows", _rowCount.ToString("N0", CultureInfo.CurrentCulture)));
            if (_location.IsRepo)
            {
                lines.Add(new("Created", "the date of the branch's first commit not on the default branch"));
            }

            if (_api.RateLimit is { } limit)
            {
                lines.Add(new("Requests left", $"{limit.Remaining:N0} until {limit.Reset.ToLocalTime():t}"));
            }

            return lines;
        }
    }

    private static PanelColumn Created { get; } = new(CreatedKey, "Created", 128, AlignRight: true);

    private static PanelColumn LastCommit { get; } = new(PanelColumn.ModifiedKey, "Last commit", 128, AlignRight: true);

    public async IAsyncEnumerable<IReadOnlyList<PanelEntry>> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Ctrl+R on the same place reads GitHub again; a refresh of filled-in dates does not.
        if (!_quietRefresh && _listed == _location)
        {
            Forget(_location);
        }

        _quietRefresh = false;
        if (_listed != _location)
        {
            RestartBackground();
        }

        _listed = _location;
        IReadOnlyList<PanelEntry> rows;
        try
        {
            rows = await RowsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SignInExpiredException)
        {
            _signedOut();
            throw;
        }

        _rowCount = rows.Count;
        yield return rows;
    }

    public async Task<bool> EnterAsync(PanelEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsFolder)
        {
            return false;
        }

        _location = _location switch
        {
            { IsTop: true } => new GitHubLocation(entry.Name),
            { IsOwner: true } => _location with { Repo = entry.Name },
            { IsRepo: true } => _location with { Branch = entry.Name },
            _ => _location.Child(entry.Name),
        };
        return await Task.FromResult(true).ConfigureAwait(false);
    }

    public Task<bool> GoUpAsync(CancellationToken cancellationToken)
    {
        if (_location.IsTop)
        {
            return Task.FromResult(false);
        }

        _location = _location.Parent;
        return Task.FromResult(true);
    }

    public async Task<OperationOutcome> GetFilesAsync(TransferRequest request, IOperationProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        if (!_location.IsBranch)
        {
            throw new PluginException("Only files inside a branch can be copied. Go into a repository and a branch first.");
        }

        var place = _location;
        var files = new List<(TreeItem Item, string Relative)>();
        foreach (var entry in request.Entries)
        {
            if (entry.Tag is not TreeItem item)
            {
                continue;
            }

            if (!item.IsFolder)
            {
                files.Add((item, item.Name));
                continue;
            }

            foreach (var inner in await AllFilesUnderAsync(place, item, cancellationToken).ConfigureAwait(false))
            {
                files.Add((inner, item.Name + "/" + inner.Path[(item.Path.Length + 1)..]));
            }
        }

        progress.SetTotals(files.Count, files.Sum(f => f.Item.Size));
        var root = Path.GetFullPath(request.LocalFolder);
        var rootWithSlash = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        int done = 0, skipped = 0, failed = 0;
        OverwriteChoice? always = null;
        foreach (var (item, relative) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Begin(relative);
            var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase))
            {
                // A name from GitHub must not lead outside the folder chosen.
                failed++;
                continue;
            }

            if (File.Exists(target))
            {
                var choice = always ?? await request.AskOverwrite(target).ConfigureAwait(false);
                if (choice == OverwriteChoice.Cancel)
                {
                    break;
                }

                if (choice is OverwriteChoice.OverwriteAll or OverwriteChoice.SkipAll)
                {
                    always = choice;
                }

                if (choice is OverwriteChoice.Skip or OverwriteChoice.SkipAll)
                {
                    skipped++;
                    progress.CompleteItem();
                    continue;
                }
            }

            try
            {
                await DownloadAsync(place, item, target, progress, cancellationToken).ConfigureAwait(false);
                done++;
            }
            catch (PluginException) when (files.Count > 1)
            {
                failed++;
            }
            catch (IOException) when (files.Count > 1)
            {
                failed++;
            }
            catch (IOException ex)
            {
                throw new PluginException(ex.Message, ex);
            }

            progress.CompleteItem();
        }

        return new OperationOutcome(done, skipped, failed);
    }

    /// <summary>A whole file to a temporary name next to the target, then into place: a failure leaves nothing behind.</summary>
    private async Task DownloadAsync(GitHubLocation place, TreeItem item, string target, IOperationProgress progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".habi-part";
        try
        {
            await using (var source = await _api.BlobAsync(place.Owner!, place.Repo!, item.Sha, cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress.AddBytes(read);
                }
            }

            File.Move(partial, target, overwrite: true);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        _background.Cancel();
        _background.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<IReadOnlyList<PanelEntry>> RowsAsync(CancellationToken cancellationToken)
    {
        var place = _location;
        if (place.IsTop || place.IsOwner)
        {
            var (login, repos) = _repos ??= await _api.RepositoriesAsync(cancellationToken).ConfigureAwait(false);
            if (place.IsTop)
            {
                // You first, then the organizations and other owners by name.
                return [.. repos.GroupBy(r => r.Owner, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new PanelEntry(g.Key, IsFolder: true)
                    {
                        Group = string.Equals(g.Key, login, StringComparison.OrdinalIgnoreCase) ? 0 : 1,
                        Cells = new Dictionary<string, CellValue> { [ReposKey] = Count(g.Count()) },
                    })];
            }

            var mine = repos.Where(r => string.Equals(r.Owner, place.Owner, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count == 0)
            {
                throw new PluginException($"No repositories of {place.Owner} are visible to you.");
            }

            return [.. mine.Select(r => new PanelEntry(r.Name, IsFolder: true, Modified: r.LastCommit)
            {
                Cells = new Dictionary<string, CellValue> { [CreatedKey] = DateCell(r.Created), [KindKey] = Kind(r) },
                Tone = r.IsArchived ? RowTone.Dim : RowTone.Normal,
                Tag = r,
            })];
        }

        if (place.IsRepo)
        {
            var repo = await RepoAsync(place, cancellationToken).ConfigureAwait(false);
            var branches = await BranchesAsync(place, cancellationToken).ConfigureAwait(false);
            if (branches.Count == 0)
            {
                throw new PluginException($"{place.Owner}/{place.Repo} has no branches yet.");
            }

            var missing = new List<BranchInfo>();
            var rows = new List<PanelEntry>();
            foreach (var branch in branches)
            {
                var isDefault = string.Equals(branch.Name, repo?.DefaultBranch, StringComparison.Ordinal);
                var cells = new Dictionary<string, CellValue> { [AuthorKey] = branch.Author ?? string.Empty };
                if (isDefault)
                {
                    cells[CreatedKey] = repo is null ? Missing : DateCell(repo.Created);
                    cells[AheadKey] = new CellValue("default", null);
                }
                else if (_compares.TryGetValue(CompareKey(place, branch.Name), out var compare))
                {
                    cells[CreatedKey] = compare.FirstOwnCommit is { } first ? DateCell(first) : new CellValue("—", null);
                    cells[AheadKey] = Count(compare.Ahead);
                    cells[BehindKey] = Count(compare.Behind);
                }
                else
                {
                    cells[CreatedKey] = new CellValue("…", null);
                    if (repo?.DefaultBranch is not null)
                    {
                        missing.Add(branch);
                    }
                }

                rows.Add(new PanelEntry(branch.Name, IsFolder: true, Modified: branch.LastCommit) { Cells = cells, Group = isDefault ? 0 : 1, Tag = branch });
            }

            if (missing.Count > 0 && repo?.DefaultBranch is { } defaultBranch)
            {
                FillCompares(place, defaultBranch, missing);
            }

            return rows;
        }

        var items = await FolderAsync(place, cancellationToken).ConfigureAwait(false);
        var undated = items.Where(i => !_fileDates.ContainsKey(FileKey(place, i.Path))).Select(i => i.Path).ToList();
        if (undated.Count > 0)
        {
            FillFileDates(place, undated);
        }

        return [.. items.Select(i => new PanelEntry(i.Name, i.IsFolder, i.IsFolder ? null : i.Size,
            _fileDates.TryGetValue(FileKey(place, i.Path), out var date) ? date : null) { Tag = i })];
    }

    private async Task<RepoInfo?> RepoAsync(GitHubLocation place, CancellationToken cancellationToken)
    {
        var (_, repos) = _repos ??= await _api.RepositoriesAsync(cancellationToken).ConfigureAwait(false);
        return repos.FirstOrDefault(r => string.Equals(r.Owner, place.Owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Name, place.Repo, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<BranchInfo>> BranchesAsync(GitHubLocation place, CancellationToken cancellationToken)
    {
        var key = place.Owner + "/" + place.Repo;
        if (!_branches.TryGetValue(key, out var branches))
        {
            branches = await _api.BranchesAsync(place.Owner!, place.Repo!, cancellationToken).ConfigureAwait(false);
            _branches[key] = branches;
        }

        return branches;
    }

    /// <summary>
    /// A folder of a branch: from the whole tree, read once per commit; when GitHub cut a large tree short, folder by
    /// folder instead.
    /// </summary>
    private async Task<IReadOnlyList<TreeItem>> FolderAsync(GitHubLocation place, CancellationToken cancellationToken)
    {
        var branch = (await BranchesAsync(place, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(b => string.Equals(b.Name, place.Branch, StringComparison.Ordinal))
            ?? throw new PluginException($"There is no branch \"{place.Branch}\" in {place.Owner}/{place.Repo} any more.");
        if (branch.TreeSha is not { } treeSha)
        {
            return [];
        }

        var tree = _trees.TryGetValue(treeSha, out var cached) ? cached : _trees[treeSha] = await _api.TreeAsync(place.Owner!, place.Repo!, treeSha, recursive: true, cancellationToken).ConfigureAwait(false);
        if (!tree.Truncated)
        {
            var prefix = place.Path.Length == 0 ? string.Empty : place.Path + "/";
            if (prefix.Length > 0 && !tree.Items.Any(i => i.IsFolder && i.Path == place.Path))
            {
                throw new PluginException($"There is no folder \"{place.Path}\" in {place.Branch} any more.");
            }

            return [.. tree.Items.Where(i => i.Path.StartsWith(prefix, StringComparison.Ordinal) && !i.Path.AsSpan(prefix.Length).Contains('/'))];
        }

        // Folder by folder, each by its own tree, found from its parent.
        var folderKey = treeSha + ":" + place.Path;
        if (_folders.TryGetValue(folderKey, out var listed))
        {
            return listed;
        }

        var sha = treeSha;
        if (place.Path.Length > 0)
        {
            var parent = await FolderAsync(place.Parent, cancellationToken).ConfigureAwait(false);
            sha = parent.FirstOrDefault(i => i.IsFolder && i.Path == place.Path)?.Sha
                ?? throw new PluginException($"There is no folder \"{place.Path}\" in {place.Branch} any more.");
        }

        var level = await _api.TreeAsync(place.Owner!, place.Repo!, sha, recursive: false, cancellationToken).ConfigureAwait(false);
        var prefixed = level.Items.Select(i => i with { Path = place.Path.Length == 0 ? i.Path : place.Path + "/" + i.Path }).ToList();
        _folders[folderKey] = prefixed;
        return prefixed;
    }

    private async Task<List<TreeItem>> AllFilesUnderAsync(GitHubLocation place, TreeItem folder, CancellationToken cancellationToken)
    {
        var inside = place with { Path = folder.Path };
        var result = new List<TreeItem>();
        foreach (var item in await FolderAsync(inside, cancellationToken).ConfigureAwait(false))
        {
            if (item.IsFolder)
            {
                result.AddRange(await AllFilesUnderAsync(place, item, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>When each branch began, a few at a time, refreshing the panel as answers come.</summary>
    private void FillCompares(GitHubLocation place, string defaultBranch, List<BranchInfo> branches)
    {
        var token = _background.Token;
        _ = Task.Run(async () =>
        {
            await Parallel.ForEachAsync(branches, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (branch, ct) =>
            {
                try
                {
                    _compares[CompareKey(place, branch.Name)] = await _api.CompareAsync(place.Owner!, place.Repo!, defaultBranch, branch.Name, ct).ConfigureAwait(false);
                }
                catch (PluginException)
                {
                    // Unrelated histories and the like: "—", and the rest go on.
                    _compares[CompareKey(place, branch.Name)] = new CompareInfo(null, 0, 0);
                }

                QueueRefresh();
            }).ConfigureAwait(false);
        }, token);
    }

    private void FillFileDates(GitHubLocation place, List<string> paths)
    {
        var token = _background.Token;
        _ = Task.Run(async () =>
        {
            for (var start = 0; start < paths.Count && !token.IsCancellationRequested; start += GitHubApi.PathsPerQuery)
            {
                var batch = paths.Skip(start).Take(GitHubApi.PathsPerQuery).ToList();
                IReadOnlyDictionary<string, DateTime> dates;
                try
                {
                    dates = await _api.LastCommitsAsync(place.Owner!, place.Repo!, place.Branch!, batch, token).ConfigureAwait(false);
                }
                catch (PluginException)
                {
                    return;
                }

                foreach (var path in batch)
                {
                    _fileDates[FileKey(place, path)] = dates.TryGetValue(path, out var date) ? date : null;
                }

                QueueRefresh();
            }
        }, token);
    }

    /// <summary>One refresh for answers arriving close together.</summary>
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
        {
            return;
        }

        var token = _background.Token;
        _ = Task.Run(async () =>
        {
            await Task.Delay(RefreshEvery, token).ConfigureAwait(false);
            Volatile.Write(ref _refreshQueued, 0);
            _quietRefresh = true;
            _host.Refresh(this);
        }, token);
    }

    private void RestartBackground()
    {
        _background.Cancel();
        _background.Dispose();
        _background = new CancellationTokenSource();
        Volatile.Write(ref _refreshQueued, 0);
    }

    /// <summary>Forgets what was read for this place, so it is read again.</summary>
    private void Forget(GitHubLocation place)
    {
        if (place.IsTop || place.IsOwner)
        {
            _repos = null;
        }
        else if (place.IsRepo)
        {
            _branches.TryRemove(place.Owner + "/" + place.Repo, out _);
            foreach (var key in _compares.Keys.Where(k => k.StartsWith(place.Owner + "/" + place.Repo + "\n", StringComparison.OrdinalIgnoreCase)))
            {
                _compares.TryRemove(key, out _);
            }
        }
        else
        {
            _branches.TryRemove(place.Owner + "/" + place.Repo, out _);
            foreach (var key in _fileDates.Keys.Where(k => k.StartsWith(place.Owner + "/" + place.Repo + "\n" + place.Branch + "\n", StringComparison.Ordinal)))
            {
                _fileDates.TryRemove(key, out _);
            }
        }
    }

    private Task OpenInBrowserAsync(PanelCommandContext context)
    {
        var url = UrlFor(context.Current);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>The page of the entry under the cursor, or of the current place on "..".</summary>
    internal string UrlFor(PanelEntry? entry)
    {
        var place = entry is null ? _location : _location switch
        {
            { IsTop: true } => new GitHubLocation(entry.Name),
            { IsOwner: true } => _location with { Repo = entry.Name },
            { IsRepo: true } => _location with { Branch = entry.Name },
            _ => _location.Child(entry.Name),
        };
        var url = "https://github.com/";
        if (place.Owner is null)
        {
            return url;
        }

        url += Uri.EscapeDataString(place.Owner);
        if (place.Repo is null)
        {
            return url;
        }

        url += "/" + Uri.EscapeDataString(place.Repo);
        if (place.Branch is null)
        {
            return url;
        }

        var isFile = entry is { IsFolder: false };
        url += (isFile ? "/blob/" : "/tree/") + string.Join('/', place.Branch.Split('/').Select(Uri.EscapeDataString));
        return place.Path.Length == 0 ? url : url + "/" + string.Join('/', place.Path.Split('/').Select(Uri.EscapeDataString));
    }

    private async Task ShowCloneAddressAsync(PanelCommandContext context)
    {
        var place = _location;
        var repo = place.Repo ?? (place.IsOwner ? context.Current?.Name : null);
        if (place.Owner is null || repo is null)
        {
            throw new PluginException("Put the cursor on a repository, or go into one, to see its clone address.");
        }

        var https = $"https://github.com/{place.Owner}/{repo}.git";
        await _host.ViewTextAsync($"Clone {place.Owner}/{repo}", $"{https}\ngit@github.com:{place.Owner}/{repo}.git\n\ngit clone {https}\n").ConfigureAwait(false);
    }

    private Task RefreshAsync(PanelCommandContext context)
    {
        _repos = null;
        _branches.Clear();
        _compares.Clear();
        _trees.Clear();
        _folders.Clear();
        _fileDates.Clear();
        _listed = null;
        _host.Refresh(this);
        return Task.CompletedTask;
    }

    private async Task SignOutAsync(PanelCommandContext context)
    {
        if (await _host.Dialogs.ConfirmAsync("Sign out of GitHub", "Forget the GitHub sign-in on this computer? The next time the panel opens it asks to sign in again.", confirmText: "Sign out").ConfigureAwait(false))
        {
            _signedOut();
            _host.ShowStatus("Signed out of GitHub.");
        }
    }

    private static string CompareKey(GitHubLocation place, string branch) => place.Owner + "/" + place.Repo + "\n" + branch;

    private static string FileKey(GitHubLocation place, string path) => place.Owner + "/" + place.Repo + "\n" + place.Branch + "\n" + path;

    private static CellValue Missing { get; } = new("", null);

    /// <summary>As the host shows dates in its own columns, so Created and Last commit read alike.</summary>
    private static CellValue DateCell(DateTime utc) => new(utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), utc);

    private static CellValue Count(int count) => new(count.ToString("N0", CultureInfo.CurrentCulture), count);

    private static string Kind(RepoInfo repo) =>
        string.Join(", ", new[] { repo.IsPrivate ? "private" : "public", repo.IsFork ? "fork" : null, repo.IsArchived ? "archived" : null }.Where(k => k is not null));
}
