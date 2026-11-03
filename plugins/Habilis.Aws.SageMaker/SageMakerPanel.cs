using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using Amazon.SageMaker.Model;
using Habilis.Aws.Common;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.SageMaker;

/// <summary>
/// The training jobs of one account and region (issue #47), like the console's Training jobs page: newest first,
/// running jobs on top, status, warm pool and an estimated cost; a summary (F3) and the full record (Shift+F3);
/// F5 saves the full records as JSON files. Read-only: it only lists and describes jobs.
/// </summary>
internal sealed class SageMakerPanel : IPanelProvider, IPanelCommands, IViewEntry, IGetFiles
{
    public const string CreatedKey = "created";
    public const string DurationKey = "duration";
    public const string StatusKey = "status";
    public const string SecondaryKey = "secondary";
    public const string WarmPoolKey = "warmpool";
    public const string WarmPoolLeftKey = "warmpoolleft";
    public const string CostKey = "cost";
    private const int ParallelRequests = 8;

    private static readonly PanelColumn Created = new(CreatedKey, "Created", 136, AlignRight: true);
    private static readonly PanelColumn Duration = new(DurationKey, "Duration", 96, AlignRight: true);
    private static readonly PanelColumn Status = new(StatusKey, "Status", 88);
    private static readonly PanelColumn Secondary = new(SecondaryKey, "Secondary status", 128);
    private static readonly PanelColumn WarmPool = new(WarmPoolKey, "Warm pool", 88);
    private static readonly PanelColumn WarmPoolLeft = new(WarmPoolLeftKey, "Pool time left", 104, AlignRight: true);
    private static readonly PanelColumn Cost = new(CostKey, "Est. cost", 88, AlignRight: true);

    private readonly IPluginHost _host;
    private readonly Func<AwsSession, ISageMakerApi> _connect;
    private readonly LivePrices _prices;
    private readonly Func<DateTime> _clock;
    private SageMakerSettings _settings;
    private AwsSession _session;
    private ISageMakerApi? _api;
    private Summary? _summary;

    public SageMakerPanel(IPluginHost host, SageMakerSettings settings, AwsSession session, Func<AwsSession, ISageMakerApi> connect, LivePrices prices, Func<DateTime> clock)
    {
        _host = host;
        _settings = settings;
        _session = session;
        _connect = connect;
        _prices = prices;
        _clock = clock;
        Commands =
        [
            new PanelCommand("Full record", "Shift+F3", FullRecordAsync),
            new PanelCommand("Filter", "Ctrl+I", FilterAsync),
            new PanelCommand("Switch profile and region", "Shift+F2", SwitchAsync),
            new PanelCommand("Settings", null, ConfigureAsync),
        ];
    }

    public string Title => $"SageMaker · {_session.Profile} · {_session.Region}";

    public string Location => $"sm:{_session.Profile}/{_session.Region}";

    public IReadOnlyList<PanelLayout> Layouts { get; } =
    [
        new("Console", [Created, Duration, Status, Secondary, WarmPool, WarmPoolLeft, Cost]),
        new("Brief", [Created, Status, Cost]),
        new("Status", [Status, Secondary, WarmPool, WarmPoolLeft]),
    ];

    public IReadOnlyList<PanelSortKey> SortKeys { get; } =
    [
        new(CreatedKey, "Creation time"),
        new(DurationKey, "Duration"),
        new(StatusKey, "Status"),
        new(CostKey, "Estimated cost"),
    ];

    public IReadOnlyList<PanelCommand> Commands { get; }

    /// <summary>F5 saves job records; there is nothing to move.</summary>
    public bool CanMove => false;

    public IReadOnlyList<InfoLine> Info
    {
        get
        {
            var lines = new List<InfoLine>
            {
                new("Profile", _session.Profile),
                new("Region", _session.Region),
                new("Name contains", _settings.NameContains.Length > 0 ? _settings.NameContains : "(any name)"),
                new("Status", _settings.StatusFilter.Length > 0 ? _settings.StatusFilter : "(any status)"),
            };
            if (_summary is { } summary)
            {
                lines.Add(new("Jobs shown", string.Create(CultureInfo.CurrentCulture, $"{summary.Shown:N0} (most recent, up to {_settings.MaxJobs:N0})")));
                lines.Add(new("Loaded at", summary.LoadedAt.ToLocalTime().ToString("T", CultureInfo.CurrentCulture)));
            }
            else
            {
                lines.Add(new("Jobs", "loading…"));
            }

            lines.Add(new("Credentials", _session.CredentialsDescription));
            return lines;
        }
    }

    private ISageMakerApi Api => _api ??= _connect(_session);

    public async IAsyncEnumerable<IReadOnlyList<PanelEntry>> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _summary = null;
        var max = _settings.MaxJobs;
        var shown = 0;
        var query = new JobQuery(_settings.NameContains, _settings.StatusFilter, max);
        await using var pages = Api.ListTrainingJobsAsync(query, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (shown < max)
        {
            List<TrainingJobSummary> page;
            try
            {
                if (!await pages.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                // Bounded memory: never more than the configured number of jobs, whatever the service returns.
                page = pages.Current.Take(max - shown).ToList();
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw AwsErrors.Describe(ex, "Listing the training jobs", _session);
            }

            if (page.Count == 0)
            {
                continue;
            }

            var jobs = await DescribeAllAsync(page.Select(s => s.TrainingJobName).ToList(), cancellationToken).ConfigureAwait(false);
            var prices = await PricesForAsync(jobs, cancellationToken).ConfigureAwait(false);
            var now = _clock();
            var entries = jobs.Select(job => Entry(job, JobCost.Estimate(job, prices, now), now, _settings.ColorRows)).ToList();
            shown += entries.Count;
            yield return entries;
        }

        _summary = new Summary(shown, _clock());
    }

    public Task<bool> EnterAsync(PanelEntry entry, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<bool> GoUpAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>F3 / Enter: the job's summary, from a fresh description so a running job shows its current progress.</summary>
    public async Task<(string Title, string Text)?> ViewAsync(PanelEntry entry, CancellationToken cancellationToken)
    {
        var job = await DescribeAsync(entry.Name, cancellationToken).ConfigureAwait(false);
        var prices = await PricesForAsync([job], cancellationToken).ConfigureAwait(false);
        var now = _clock();
        return (entry.Name, JobText.Summary(job, JobCost.Estimate(job, prices, now), now));
    }

    private Task<PriceTable> PricesForAsync(IEnumerable<DescribeTrainingJobResponse> jobs, CancellationToken cancellationToken) =>
        _prices.ForAsync(jobs.SelectMany(JobFacts.Instances).Select(i => i.Type), _session, cancellationToken);

    /// <summary>
    /// F5: writes <c>&lt;job name&gt;.json</c> (the full description) for each job into the local folder. Each file is
    /// written to a temporary name and moved into place, so a failure or a cancel never leaves a partial file.
    /// </summary>
    public async Task<OperationOutcome> GetFilesAsync(TransferRequest request, IOperationProgress progress, CancellationToken cancellationToken)
    {
        progress.SetTotals(request.Entries.Count, null);
        int saved = 0, skipped = 0, failed = 0;
        var problems = new List<string>();
        OverwriteChoice? forAll = null;
        foreach (var entry in request.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Begin(entry.Name);
            var fileName = FileNameOf(entry.Name);
            var target = Path.Combine(request.LocalFolder, fileName);
            var overwrite = false;
            if (File.Exists(target))
            {
                var choice = forAll ?? await request.AskOverwrite(fileName).ConfigureAwait(false);
                switch (choice)
                {
                    case OverwriteChoice.OverwriteAll:
                        forAll = OverwriteChoice.Overwrite;
                        break;
                    case OverwriteChoice.SkipAll:
                        forAll = OverwriteChoice.Skip;
                        break;
                    case OverwriteChoice.Cancel:
                        return new OperationOutcome(saved, skipped, failed, Report(problems, "Cancelled."));
                }

                if (choice is OverwriteChoice.Skip or OverwriteChoice.SkipAll)
                {
                    skipped++;
                    progress.CompleteItem();
                    continue;
                }

                overwrite = true;
            }

            try
            {
                var job = await DescribeAsync(entry.Name, cancellationToken).ConfigureAwait(false);
                await WriteAtomicallyAsync(target, JobText.FullRecord(job), overwrite, cancellationToken).ConfigureAwait(false);
                saved++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failed++;
                problems.Add(ex is PluginException ? ex.Message : $"{fileName}: {ex.Message}");
            }

            progress.CompleteItem();
        }

        return new OperationOutcome(saved, skipped, failed, Report(problems, null));
    }

    public ValueTask DisposeAsync()
    {
        _api?.Dispose();
        _api = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>Called by the plugin after the settings changed: the next listing uses them.</summary>
    internal void Apply(SageMakerSettings settings)
    {
        _settings = settings;
        Reconnect(settings.Session(_session.Profile, _session.Region));
    }

    /// <summary>A panel entry for a job: the name, the creation time, the console's columns, running jobs first.</summary>
    internal static PanelEntry Entry(DescribeTrainingJobResponse job, CostEstimate cost, DateTime now, bool colorRows)
    {
        var status = JobFacts.Status(job);
        var cells = new Dictionary<string, CellValue>
        {
            [StatusKey] = status,
            [CostKey] = cost.Amount is { } amount ? new CellValue(cost.Text, amount) : new CellValue(cost.Text),
        };
        DateTime? created = job.CreationTime is { } time ? JobFacts.Utc(time) : null;
        if (created is { } utc)
        {
            cells[CreatedKey] = new CellValue(utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), utc);
        }

        if (JobFacts.Duration(job, now) is { } duration)
        {
            cells[DurationKey] = new CellValue(AwsText.Duration(duration), duration);
        }

        if (JobFacts.SecondaryStatus(job) is { Length: > 0 } secondary)
        {
            cells[SecondaryKey] = secondary;
        }

        if (job.WarmPoolStatus?.Status?.Value is { Length: > 0 } pool)
        {
            cells[WarmPoolKey] = pool;
        }

        if (JobFacts.WarmPoolLeft(job, now) is { } left)
        {
            cells[WarmPoolLeftKey] = new CellValue(AwsText.Duration(left), left);
        }

        return new PanelEntry(job.TrainingJobName ?? string.Empty, IsFolder: false, Modified: created)
        {
            Cells = cells,
            Group = JobFacts.IsRunning(job) ? 0 : 1,
            Tone = colorRows ? ToneOf(status) : RowTone.Normal,
            Tag = job,
        };
    }

    internal static RowTone ToneOf(string status) => status switch
    {
        JobFacts.Completed => RowTone.Good,
        JobFacts.Failed => RowTone.Bad,
        JobFacts.Stopped => RowTone.Dim,
        JobFacts.InProgress => RowTone.Accent,
        JobFacts.Stopping => RowTone.Warning,
        _ => RowTone.Normal,
    };

    /// <summary>The file name for a job's record; job names are letters, digits and hyphens, but stay safe anyway.</summary>
    internal static string FileNameOf(string jobName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(jobName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()) + ".json";
    }

    /// <summary>Describes the jobs, at most <see cref="ParallelRequests"/> at a time; the first failure stops the rest.</summary>
    private async Task<DescribeTrainingJobResponse[]> DescribeAllAsync(List<string> names, CancellationToken cancellationToken)
    {
        using var failure = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var gate = new SemaphoreSlim(ParallelRequests);
        var tasks = names.Select(async name =>
        {
            await gate.WaitAsync(failure.Token).ConfigureAwait(false);
            try
            {
                return await DescribeAsync(name, failure.Token).ConfigureAwait(false);
            }
            catch when (!failure.IsCancellationRequested)
            {
                await failure.CancelAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        try
        {
            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // Report the failure that stopped the others, not a sibling's cancellation.
            var first = tasks.Where(t => t.IsFaulted).SelectMany(t => t.Exception!.InnerExceptions).FirstOrDefault(e => e is not OperationCanceledException);
            if (first is not null)
            {
                ExceptionDispatchInfo.Throw(first);
            }

            throw;
        }
    }

    private async Task<DescribeTrainingJobResponse> DescribeAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            return await Api.DescribeTrainingJobAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw AwsErrors.Describe(ex, $"Reading training job {name}", _session);
        }
    }

    private static async Task WriteAtomicallyAsync(string target, string text, bool overwrite, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(target)!;
        var temporary = Path.Combine(folder, $".{Path.GetFileName(target)}.{Guid.NewGuid().ToString("N")[..8]}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, target, overwrite);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the original error is what the user needs to see.
            }

            throw;
        }
    }

    /// <summary>Shift+F3: the complete description as returned by the SageMaker API.</summary>
    private async Task FullRecordAsync(PanelCommandContext context)
    {
        if ((context.Current ?? (context.Selected.Count > 0 ? context.Selected[0] : null)) is not { } entry)
        {
            return;
        }

        var job = await DescribeAsync(entry.Name, context.CancellationToken).ConfigureAwait(false);
        await _host.ViewTextAsync($"{entry.Name}.json", JobText.FullRecord(job)).ConfigureAwait(false);
    }

    /// <summary>Ctrl+I: filter by part of the name and by status; kept in the settings. The host re-lists afterwards.</summary>
    private async Task FilterAsync(PanelCommandContext context)
    {
        var form = SageMakerSettings.FilterForm(_settings);
        if (!await _host.Dialogs.EditAsync(form).ConfigureAwait(false))
        {
            return;
        }

        // Saved on top of the stored settings, so a filter never overwrites a change made in another panel's settings.
        var settings = SageMakerSettings.Load(_host.Settings).WithFilter(form);
        settings.Save(_host.Settings);
        _settings = _settings.WithFilter(form);
        _summary = null;
    }

    /// <summary>Shift+F2: another profile and region for this panel.</summary>
    private async Task SwitchAsync(PanelCommandContext context)
    {
        var profiles = AwsProfiles.List(_settings.CredentialsFile);
        var profile = await _host.Dialogs.ChooseAsync("AWS profile", profiles.Count > 0 ? profiles : [_session.Profile], _session.Profile).ConfigureAwait(false);
        if (profile is null)
        {
            return;
        }

        var regions = _settings.Regions.Concat(AwsRegions.Commercial).Distinct().ToList();
        var region = await _host.Dialogs.ChooseAsync("Region", regions, _session.Region).ConfigureAwait(false);
        if (region is null)
        {
            return;
        }

        if (!AwsRegions.IsValid(region))
        {
            throw new PluginException($"\"{region}\" is not a valid AWS region name, e.g. eu-west-1.");
        }

        Reconnect(_settings.Session(profile, region));
    }

    private async Task ConfigureAsync(PanelCommandContext context)
    {
        var form = SageMakerSettings.Form(_settings, AwsProfiles.List(_settings.CredentialsFile));
        if (await _host.Dialogs.EditAsync(form).ConfigureAwait(false))
        {
            var settings = SageMakerSettings.FromForm(form);
            settings.Save(_host.Settings);
            _settings = settings;
            Reconnect(settings.Session());
        }
    }

    private void Reconnect(AwsSession session)
    {
        _api?.Dispose();
        _api = null;
        _session = session;
        _summary = null;
    }

    private static string? Report(List<string> problems, string? last)
    {
        var lines = last is null ? problems : [.. problems, last];
        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : null;
    }

    private sealed record Summary(int Shown, DateTime LoadedAt);
}
