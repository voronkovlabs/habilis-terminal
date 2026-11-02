using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Amazon.SQS.Model;
using Habilis.Aws.Common;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.Sqs;

/// <summary>
/// The queues of one account and region (issue #48): name, messages available and in flight; purge selected queues
/// after a confirmation naming them; switch profile and region; a summary in the info panel.
/// </summary>
internal sealed class SqsPanel : IPanelProvider, IPanelCommands, IViewEntry
{
    public const string AvailableKey = "available";
    public const string InFlightKey = "inflight";
    private const int ParallelRequests = 8;

    private readonly IPluginHost _host;
    private readonly Func<AwsSession, ISqsApi> _connect;
    private SqsSettings _settings;
    private AwsSession _session;
    private ISqsApi? _api;
    private Summary? _summary;

    public SqsPanel(IPluginHost host, SqsSettings settings, AwsSession session, Func<AwsSession, ISqsApi> connect)
    {
        _host = host;
        _settings = settings;
        _session = session;
        _connect = connect;
        Commands =
        [
            new PanelCommand("Purge queues (remove all messages)", "F8", PurgeAsync),
            new PanelCommand("Switch profile and region", "Shift+F2", SwitchAsync),
            new PanelCommand("Settings", null, ConfigureAsync),
        ];
    }

    public string Title => $"SQS · {_session.Profile} · {_session.Region}";

    public string Location => $"sqs:{_session.Profile}/{_session.Region}";

    public IReadOnlyList<PanelLayout> Layouts { get; } =
    [
        new("Queues", [new PanelColumn(AvailableKey, "Available", 96, AlignRight: true), new PanelColumn(InFlightKey, "In flight", 96, AlignRight: true)]),
        new("Names", []),
    ];

    public IReadOnlyList<PanelSortKey> SortKeys { get; } = [new(AvailableKey, "Messages available"), new(InFlightKey, "Messages in flight")];

    public IReadOnlyList<PanelCommand> Commands { get; }

    public IReadOnlyList<InfoLine> Info
    {
        get
        {
            var lines = new List<InfoLine>
            {
                new("Profile", _session.Profile),
                new("Region", _session.Region),
                new("Name prefix", _settings.NamePrefix.Length > 0 ? _settings.NamePrefix : "(all queues)"),
                new("Hidden by", _settings.Exclusions.Count > 0 ? string.Join(", ", _settings.Exclusions) : "(nothing)"),
            };
            if (_summary is { } summary)
            {
                lines.Add(new("Queues shown", summary.Shown.ToString("N0", CultureInfo.CurrentCulture)));
                lines.Add(new("Queues hidden", summary.Hidden.ToString("N0", CultureInfo.CurrentCulture)));
                lines.Add(new("Loaded at", summary.LoadedAt.ToLocalTime().ToString("T", CultureInfo.CurrentCulture)));
            }
            else
            {
                lines.Add(new("Queues", "loading…"));
            }

            lines.Add(new("Credentials", _session.CredentialsDescription));
            return lines;
        }
    }

    private ISqsApi Api => _api ??= _connect(_session);

    public async IAsyncEnumerable<IReadOnlyList<PanelEntry>> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _summary = null;
        var shown = 0;
        var hidden = 0;
        await using var pages = Api.ListQueueUrlsAsync(_settings.NamePrefix, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            IReadOnlyList<string> page;
            try
            {
                if (!await pages.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                page = pages.Current;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw AwsErrors.Describe(ex, "Listing the queues", _session);
            }

            var visible = new List<(string Url, string Name)>();
            foreach (var url in page)
            {
                var name = NameOf(url);
                if (_settings.Exclusions.Any(pattern => AwsText.MatchesWildcard(name, pattern)))
                {
                    hidden++;
                }
                else
                {
                    visible.Add((url, name));
                }
            }

            var entries = await CountAsync(visible, cancellationToken).ConfigureAwait(false);
            shown += entries.Count;
            yield return entries;
        }

        _summary = new Summary(shown, hidden, DateTime.UtcNow);
    }

    public Task<bool> EnterAsync(PanelEntry entry, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<bool> GoUpAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>F3 / Enter: the queue's name, URL and counts.</summary>
    public Task<(string Title, string Text)?> ViewAsync(PanelEntry entry, CancellationToken cancellationToken)
    {
        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Queue:              {entry.Name}")
            .AppendLine(CultureInfo.InvariantCulture, $"URL:                {entry.Tag}")
            .AppendLine(CultureInfo.InvariantCulture, $"Messages available: {Cell(entry, AvailableKey)}")
            .AppendLine(CultureInfo.InvariantCulture, $"Messages in flight: {Cell(entry, InFlightKey)}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Profile {_session.Profile}, region {_session.Region}. Counts are approximate, as Amazon SQS reports them.")
            .ToString();
        return Task.FromResult<(string, string)?>((entry.Name, text));
    }

    public ValueTask DisposeAsync()
    {
        _api?.Dispose();
        _api = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>Called by the plugin after the settings changed: the next listing uses them.</summary>
    internal void Apply(SqsSettings settings)
    {
        _settings = settings;
        Reconnect(settings.Session(_session.Profile, _session.Region));
    }

    private async Task<List<PanelEntry>> CountAsync(List<(string Url, string Name)> queues, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(ParallelRequests);
        var tasks = queues.Select(async queue =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var counts = await Api.GetCountsAsync(queue.Url, cancellationToken).ConfigureAwait(false);
                return Entry(queue.Url, queue.Name, counts);
            }
            catch (QueueDoesNotExistException)
            {
                // Deleted between listing and counting: leave it out rather than show a wrong count.
                return null;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw AwsErrors.Describe(ex, $"Reading the message counts of {queue.Name}", _session);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        var entries = await Task.WhenAll(tasks).ConfigureAwait(false);
        return entries.OfType<PanelEntry>().ToList();
    }

    internal static PanelEntry Entry(string url, string name, QueueCounts counts) => new(name)
    {
        Tag = url,
        Cells = new Dictionary<string, CellValue>
        {
            [AvailableKey] = new(counts.Available.ToString("N0", CultureInfo.CurrentCulture), counts.Available),
            [InFlightKey] = new(counts.InFlight.ToString("N0", CultureInfo.CurrentCulture), counts.InFlight),
        },
        Tone = counts.Available > 0 ? RowTone.Normal : RowTone.Dim,
    };

    /// <summary>F8: purge the selected queues, only after a confirmation that names them; the outcome per queue.</summary>
    private async Task PurgeAsync(PanelCommandContext context)
    {
        var queues = context.Selected;
        if (queues.Count == 0)
        {
            return;
        }

        var names = queues.Select(q => q.Name).ToList();
        var confirmed = await _host.Dialogs.ConfirmAsync(
            "Purge queues",
            $"Delete all messages in {(queues.Count == 1 ? "this queue" : $"these {queues.Count} queues")} ({_session.Profile}, {_session.Region})? The queues stay; their messages cannot be recovered.",
            names,
            "Purge",
            destructive: true).ConfigureAwait(false);
        if (!confirmed)
        {
            return;
        }

        context.Progress.SetTotals(queues.Count, null);
        var report = new StringBuilder();
        var failed = 0;
        foreach (var queue in queues)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"{queue.Name}: not purged (cancelled)");
                continue;
            }

            context.Progress.Begin(queue.Name);
            try
            {
                await Api.PurgeAsync((string)queue.Tag!, context.CancellationToken).ConfigureAwait(false);
                report.AppendLine(CultureInfo.InvariantCulture, $"{queue.Name}: purged");
            }
            catch (PurgeQueueInProgressException)
            {
                failed++;
                report.AppendLine(CultureInfo.InvariantCulture, $"{queue.Name}: not purged, a purge already ran in the last 60 seconds");
            }
            catch (OperationCanceledException)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"{queue.Name}: not purged (cancelled)");
            }
            catch (Exception ex)
            {
                failed++;
                report.AppendLine(CultureInfo.InvariantCulture, $"{queue.Name}: not purged. {AwsErrors.Describe(ex, "Purging", _session).Message}");
            }

            context.Progress.CompleteItem();
        }

        await _host.Dialogs.ShowMessageAsync("Purge queues", report.ToString().TrimEnd(), failed > 0 ? MessageKind.Warning : MessageKind.Information).ConfigureAwait(false);
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

        Reconnect(_settings.Session(profile, region));
    }

    private async Task ConfigureAsync(PanelCommandContext context)
    {
        var form = SqsSettings.Form(_settings, AwsProfiles.List(_settings.CredentialsFile));
        if (await _host.Dialogs.EditAsync(form).ConfigureAwait(false))
        {
            var settings = SqsSettings.FromForm(form);
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

    private static string Cell(PanelEntry entry, string key) => entry.Cells is { } cells && cells.TryGetValue(key, out var cell) ? cell.Text : "?";

    /// <summary>The queue name is the last segment of its URL.</summary>
    internal static string NameOf(string url) => url[(url.TrimEnd('/').LastIndexOf('/') + 1)..].TrimEnd('/');

    private sealed record Summary(int Shown, int Hidden, DateTime LoadedAt);
}
