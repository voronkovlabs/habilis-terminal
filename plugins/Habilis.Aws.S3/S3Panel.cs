using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Habilis.Aws.Common;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.S3;

internal enum S3Kind
{
    Bucket,
    Folder,
    Object,
}

/// <summary>
/// What a panel entry stands for: a bucket, a folder (<see cref="Key"/> is its full prefix, ending in "/") or an
/// object (its exact key, with what the listing said about it).
/// </summary>
internal sealed record S3Ref(S3Kind Kind, string Bucket, string Key, long Size = 0, string? ETag = null, string? StorageClass = null);

/// <summary>
/// Amazon S3 as a file panel (issue #46): buckets at the top, key prefixes as folders and objects as files.
/// Download (F5, F3 through a copy), upload (F5 / F6 into the panel), delete (F8) and make folder (F7); transfers
/// run in parallel and large files in parts. Switch profile with Shift+F2.
/// </summary>
internal sealed partial class S3Panel : IPanelProvider, IPanelCommands, IGetFiles, IPutFiles, IDeleteEntries, IMakeFolder
{
    public const string StorageClassKey = "class";
    private const int DeleteBatch = 1000;

    private readonly IPluginHost _host;
    private readonly Func<AwsSession, IS3Api> _connect;
    private readonly Lock _apiLock = new();

    // Bucket regions never change while the bucket exists, whatever the profile, so the cache outlives reconnects.
    private readonly ConcurrentDictionary<string, string> _regions = new(StringComparer.Ordinal);
    private S3Settings _settings;
    private AwsSession _session;
    private S3Location _location;
    private IS3Api? _api;
    private Summary? _summary;

    public S3Panel(IPluginHost host, S3Settings settings, AwsSession session, S3Location location, Func<AwsSession, IS3Api> connect)
    {
        _host = host;
        _settings = settings;
        _session = session;
        _location = location;
        _connect = connect;
        Commands =
        [
            new PanelCommand("Switch profile", "Shift+F2", SwitchProfileAsync),
            new PanelCommand("Settings", null, ConfigureAsync),
        ];
    }

    public string Title => _location.IsBucketList ? $"S3 · {_session.Profile}" : $"S3 · {_session.Profile} · {_location.Path}";

    public string Location => _location.Address;

    internal S3Location Current => _location;

    internal AwsSession Session => _session;

    public IReadOnlyList<PanelLayout> Layouts { get; } =
    [
        new("Files", [PanelColumn.Size, PanelColumn.Modified]),
        new("Storage class", [PanelColumn.Size, PanelColumn.Modified, new PanelColumn(StorageClassKey, "Class", 110)]),
        new("Names", []),
    ];

    public IReadOnlyList<PanelSortKey> SortKeys { get; } = [new(StorageClassKey, "Storage class")];

    public IReadOnlyList<PanelCommand> Commands { get; }

    public bool CanMove => false;

    public IReadOnlyList<InfoLine> Info
    {
        get
        {
            var lines = new List<InfoLine> { new("Profile", _session.Profile) };
            if (_location.Bucket is { } bucket)
            {
                lines.Add(new("Region", _regions.TryGetValue(bucket, out var region) ? region : "…"));
            }
            else
            {
                lines.Add(new("Region", $"{_session.Region} (for the bucket list)"));
            }

            lines.Add(new("Location", _location.Address));
            if (_summary is { } summary)
            {
                if (_location.IsBucketList)
                {
                    lines.Add(new("Buckets shown", Count(summary.Folders)));
                }
                else
                {
                    lines.Add(new("Folders shown", Count(summary.Folders)));
                    lines.Add(new("Objects shown", Count(summary.Objects)));
                    lines.Add(new("Size of objects", Size(summary.Bytes)));
                }

                lines.Add(new("Loaded at", summary.LoadedAt.ToLocalTime().ToString("T", CultureInfo.CurrentCulture)));
            }
            else
            {
                lines.Add(new("Contents", "loading…"));
            }

            lines.Add(new("Parallel transfers", Count(_settings.MaxParallel)));
            lines.Add(new("Part size", $"{_settings.PartSizeMb} MB"));
            lines.Add(new("Credentials", _session.CredentialsDescription));
            return lines;
        }
    }

    private IS3Api Api
    {
        get
        {
            lock (_apiLock)
            {
                return _api ??= _connect(_session);
            }
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<PanelEntry>> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _summary = null;
        var location = _location;
        var folders = 0;
        var objects = 0;
        var bytes = 0L;
        if (location.Bucket is not { } bucket)
        {
            await foreach (var page in Guard(Api.ListBucketsAsync(cancellationToken), "Listing the buckets", "s3:ListAllMyBuckets", cancellationToken).ConfigureAwait(false))
            {
                foreach (var info in page.Where(b => b.Region is not null))
                {
                    _regions[info.Name] = BucketRegions.FromLocation(info.Region);
                }

                folders += page.Count;
                yield return page.Select(BucketEntry).ToList();
            }
        }
        else
        {
            var region = await RegionOfAsync(bucket, cancellationToken).ConfigureAwait(false);
            var listing = Api.ListObjectsAsync(region, bucket, location.Prefix, recursive: false, cancellationToken);
            await foreach (var page in Guard(listing, $"Listing {location.Address}", "s3:ListBucket", cancellationToken).ConfigureAwait(false))
            {
                var entries = new List<PanelEntry>(page.Prefixes.Count + page.Objects.Count);
                entries.AddRange(page.Prefixes.Select(p => FolderEntry(bucket, location.Prefix, p)));

                // The zero-byte "folder/" object that makes an empty folder visible is the folder itself.
                foreach (var info in page.Objects.Where(o => o.Key != location.Prefix))
                {
                    entries.Add(ObjectEntry(bucket, location.Prefix, info));
                    bytes += info.Size;
                }

                folders += page.Prefixes.Count;
                objects += entries.Count - page.Prefixes.Count;
                yield return entries;
            }
        }

        _summary = new Summary(folders, objects, bytes, DateTime.UtcNow);
    }

    public Task<bool> EnterAsync(PanelEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Tag is not S3Ref { Kind: not S3Kind.Object } target)
        {
            return Task.FromResult(false);
        }

        _location = new S3Location(target.Bucket, target.Kind == S3Kind.Bucket ? string.Empty : target.Key);
        _summary = null;
        return Task.FromResult(true);
    }

    /// <summary>Folder → parent folder → bucket root → bucket list → out of the plugin.</summary>
    public Task<bool> GoUpAsync(CancellationToken cancellationToken)
    {
        if (_location.IsBucketList)
        {
            return Task.FromResult(false);
        }

        _location = _location.Up();
        _summary = null;
        return Task.FromResult(true);
    }

    /// <summary>F7: a zero-byte "name/" object, the way the AWS console makes folders.</summary>
    public async Task MakeFolderAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_location.Bucket is not { } bucket)
        {
            throw new PluginException("Creating buckets is not supported. Open a bucket to make a folder in it.");
        }

        if (name.Length == 0)
        {
            throw new PluginException("Type a name for the folder.");
        }

        if (name.Contains('/', StringComparison.Ordinal))
        {
            throw new PluginException("A folder name cannot contain \"/\". Make one folder at a time.");
        }

        if (name is "." or "..")
        {
            throw new PluginException($"\"{name}\" cannot be used as a folder name.");
        }

        var key = _location.Prefix + name + "/";
        var region = await RegionOfAsync(bucket, cancellationToken).ConfigureAwait(false);
        try
        {
            await Api.PutEmptyObjectAsync(region, bucket, key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsCancel(ex, cancellationToken))
        {
            throw Describe(ex, $"Creating the folder {name}", "s3:PutObject");
        }
    }

    /// <summary>F8 (the host has confirmed): objects, and folders with everything under them, in batches of 1000.</summary>
    public async Task<OperationOutcome> DeleteAsync(IReadOnlyList<PanelEntry> entries, IOperationProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(progress);
        if (_location.Bucket is not { } bucket || entries.Any(e => e.Tag is S3Ref { Kind: S3Kind.Bucket }))
        {
            throw new PluginException("Deleting buckets is not supported. Open a bucket to delete folders and objects in it.");
        }

        var targets = entries.Select(e => e.Tag).OfType<S3Ref>().ToList();
        var objects = targets.Where(t => t.Kind == S3Kind.Object).Select(t => t.Key).ToList();
        var folders = targets.Where(t => t.Kind == S3Kind.Folder).ToList();
        progress.SetTotals(folders.Count == 0 ? objects.Count : null, null);
        var region = await RegionOfAsync(bucket, cancellationToken).ConfigureAwait(false);
        var tally = new DeleteTally();
        foreach (var batch in objects.Chunk(DeleteBatch))
        {
            await DeleteBatchAsync(region, bucket, batch, tally, progress, cancellationToken).ConfigureAwait(false);
        }

        foreach (var folder in folders)
        {
            var listing = Api.ListObjectsAsync(region, bucket, folder.Key, recursive: true, cancellationToken);
            await foreach (var page in Guard(listing, $"Listing s3://{bucket}/{folder.Key}", "s3:ListBucket", cancellationToken).ConfigureAwait(false))
            {
                // Every page holds at most 1000 keys: one DeleteObjects call, bounded memory however big the folder.
                if (page.Objects.Count > 0)
                {
                    await DeleteBatchAsync(region, bucket, page.Objects.Select(o => o.Key).ToList(), tally, progress, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (tally.Failures.Count == 0)
        {
            return new OperationOutcome(tally.Deleted, 0, 0);
        }

        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"{Count(tally.Failed)} objects could not be deleted, {Count(tally.Deleted)} were deleted.")
            .AppendLine();
        foreach (var failure in tally.Failures.Take(20))
        {
            report.AppendLine(failure);
        }

        if (tally.Failures.Count > 20)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"… and {Count(tally.Failures.Count - 20)} more.");
        }

        await _host.Dialogs.ShowMessageAsync("Delete", report.ToString().TrimEnd(), MessageKind.Warning).ConfigureAwait(false);
        return new OperationOutcome(tally.Deleted, 0, tally.Failed, $"{tally.Failed} failed, {tally.Deleted} deleted. {tally.Failures[0]}");
    }

    public ValueTask DisposeAsync()
    {
        lock (_apiLock)
        {
            _api?.Dispose();
            _api = null;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Called by the plugin after the settings changed: the next listing and transfer use them.</summary>
    internal void Apply(S3Settings settings)
    {
        _settings = settings;
        Reconnect(settings.Session(_session.Profile));
    }

    internal static PanelEntry BucketEntry(S3BucketInfo bucket) => new(bucket.Name, IsFolder: true, Modified: bucket.Created)
    {
        Tag = new S3Ref(S3Kind.Bucket, bucket.Name, string.Empty),
    };

    internal static PanelEntry FolderEntry(string bucket, string prefix, string commonPrefix) =>
        new(KeyNames.DisplayName(KeyNames.ChildSegment(prefix, commonPrefix, isFolder: true)), IsFolder: true)
        {
            Tag = new S3Ref(S3Kind.Folder, bucket, commonPrefix),
        };

    internal static PanelEntry ObjectEntry(string bucket, string prefix, S3ObjectInfo info) =>
        new(KeyNames.DisplayName(KeyNames.ChildSegment(prefix, info.Key, isFolder: false)), IsFolder: false, info.Size, info.Modified)
        {
            Tag = new S3Ref(S3Kind.Object, bucket, info.Key, info.Size, info.ETag, info.StorageClass),
            // No storage class reported: a missing value, sorted last.
            Cells = new Dictionary<string, CellValue> { [StorageClassKey] = info.StorageClass is { } storageClass ? storageClass : new CellValue(string.Empty) },

            // Archived objects cannot be downloaded until restored: dimmed as a hint.
            Tone = IsArchived(info.StorageClass) ? RowTone.Dim : RowTone.Normal,
        };

    internal static bool IsArchived(string? storageClass) => storageClass is "GLACIER" or "DEEP_ARCHIVE";

    /// <summary>The bucket's region: from the bucket list when S3 sent it, else asked once (GetBucketLocation) and kept.</summary>
    internal async Task<string> RegionOfAsync(string bucket, CancellationToken cancellationToken)
    {
        if (_regions.TryGetValue(bucket, out var known))
        {
            return known;
        }

        string location;
        try
        {
            location = await Api.GetBucketLocationAsync(bucket, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsCancel(ex, cancellationToken))
        {
            throw Describe(ex, $"Finding the region of bucket {bucket}", "s3:GetBucketLocation");
        }

        return _regions[bucket] = BucketRegions.FromLocation(location);
    }

    private async Task DeleteBatchAsync(string region, string bucket, IReadOnlyList<string> keys, DeleteTally tally, IOperationProgress progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress.Begin(keys[0]);
        IReadOnlyList<S3DeleteFailure> failures;
        try
        {
            failures = await Api.DeleteObjectsAsync(region, bucket, keys, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsCancel(ex, cancellationToken))
        {
            throw Describe(ex, keys.Count == 1 ? $"Deleting {keys[0]}" : $"Deleting {keys.Count} objects", "s3:DeleteObject");
        }

        foreach (var failure in failures)
        {
            tally.Failures.Add($"{failure.Key}: {(failure.Code is "AccessDenied" ? "permission missing: s3:DeleteObject" : failure.Message)} ({failure.Code})");
        }

        tally.Failed += failures.Count;
        for (var i = failures.Count; i < keys.Count; i++)
        {
            tally.Deleted++;
            progress.CompleteItem();
        }
    }

    /// <summary>Shift+F2: another profile; the panel stays where it is if that profile can list it, else shows the bucket list.</summary>
    private async Task SwitchProfileAsync(PanelCommandContext context)
    {
        var profiles = AwsProfiles.List(_settings.CredentialsFile);
        var profile = await _host.Dialogs.ChooseAsync("AWS profile", profiles.Count > 0 ? profiles : [_session.Profile], _session.Profile).ConfigureAwait(false);
        if (profile is null)
        {
            return;
        }

        Reconnect(_settings.Session(profile));
        if (_location.Bucket is { } bucket && !await CanListAsync(bucket, _location.Prefix, context.CancellationToken).ConfigureAwait(false))
        {
            _host.ShowStatus($"s3://{bucket}/ cannot be listed with profile {profile}: showing the bucket list.");
            _location = S3Location.Buckets;
        }
    }

    private async Task<bool> CanListAsync(string bucket, string prefix, CancellationToken cancellationToken)
    {
        try
        {
            var region = await RegionOfAsync(bucket, cancellationToken).ConfigureAwait(false);
            await using var pages = Api.ListObjectsAsync(region, bucket, prefix, recursive: false, cancellationToken).GetAsyncEnumerator(cancellationToken);
            await pages.MoveNextAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (!IsCancel(ex, cancellationToken))
        {
            return false;
        }
    }

    private async Task ConfigureAsync(PanelCommandContext context)
    {
        var form = S3Settings.Form(_settings, AwsProfiles.List(_settings.CredentialsFile));
        if (await _host.Dialogs.EditAsync(form).ConfigureAwait(false))
        {
            var settings = S3Settings.FromForm(form);
            settings.Save(_host.Settings);
            _settings = settings;
            Reconnect(settings.Session());
        }
    }

    private void Reconnect(AwsSession session)
    {
        lock (_apiLock)
        {
            _api?.Dispose();
            _api = null;
            _session = session;
        }

        _summary = null;
    }

    /// <summary>Pages of an S3 listing with failures turned into plain-language errors.</summary>
    private async IAsyncEnumerable<T> Guard<T>(IAsyncEnumerable<T> source, string action, string permission, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var pages = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            T page;
            try
            {
                if (!await pages.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                page = pages.Current;
            }
            catch (Exception ex) when (!IsCancel(ex, cancellationToken))
            {
                throw Describe(ex, action, permission);
            }

            yield return page;
        }
    }

    private PluginException Describe(Exception error, string action, string permission) => S3Errors.Describe(error, action, _session, permission);

    /// <summary>Cancelled by the host (a timeout is also an <see cref="OperationCanceledException"/>, but an error).</summary>
    private static bool IsCancel(Exception error, CancellationToken cancellationToken) =>
        error is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private static string Count(long count) => count.ToString("N0", CultureInfo.CurrentCulture);

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"),
    };

    private sealed record Summary(int Folders, int Objects, long Bytes, DateTime LoadedAt);

    private sealed class DeleteTally
    {
        public int Deleted { get; set; }

        public int Failed { get; set; }

        public List<string> Failures { get; } = [];
    }
}
