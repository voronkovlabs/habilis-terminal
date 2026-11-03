using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Habilis.Aws.Common;
using HabilisTerminal.Plugins;

namespace Habilis.Gpu;

/// <summary>
/// GPU load of SageMaker training jobs in progress (issue #70): the jobs, and in a job its hosts with GPU, GPU memory,
/// CPU and memory load from CloudWatch (one value per minute). Refreshes itself every minute; Ctrl+Q shows the last
/// 30 minutes as charts. Read-only.
/// </summary>
internal sealed class CloudGpuPanel : IPanelProvider, IQuickViewEntry, IViewEntry
{
    private const int Minutes = 30;

    private static readonly PanelColumn Instances = new("instances", "Instances", 168);
    private static readonly PanelColumn Gpu = new(LocalGpuPanel.LoadKey, "GPU %", 72, AlignRight: true);
    private static readonly PanelColumn GpuMemory = new(LocalGpuPanel.MemoryKey, "GPU mem %", 88, AlignRight: true);
    private static readonly PanelColumn Cpu = new("cpu", "CPU %", 72, AlignRight: true);
    private static readonly PanelColumn Memory = new("ram", "Mem %", 72, AlignRight: true);

    private readonly IPluginHost _host;
    private readonly AwsSession _session;
    private readonly Func<AwsSession, ICloudGpuApi> _connect;
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private ICloudGpuApi? _api;
    private IReadOnlyList<CloudJob> _jobs = [];
    private CloudJob? _job;
    private IReadOnlyList<HostMetrics> _metrics = [];
    private Task? _timer;

    public CloudGpuPanel(IPluginHost host, AwsSession session, Func<AwsSession, ICloudGpuApi> connect, Func<DateTime> clock, TimeSpan interval)
    {
        _host = host;
        _session = session;
        _connect = connect;
        _clock = clock;
        _interval = interval;
    }

    public string Title => $"GPU monitor · SageMaker · {_session.Profile} · {_session.Region}";

    /// <summary>The job list is flat (no ".."); inside a job ".." goes back to it (issue #77).</summary>
    public bool ShowsParent => _job is not null;

    public string Location => $"gpu:sm:{_session.Profile}/{_session.Region}" + (_job is null ? string.Empty : "/" + _job.Name);

    public IReadOnlyList<PanelLayout> Layouts { get; } = [new("Monitor", [Instances, Gpu, GpuMemory, Cpu, Memory])];

    public IReadOnlyList<PanelSortKey> SortKeys { get; } = [new(LocalGpuPanel.LoadKey, "GPU load")];

    public IReadOnlyList<InfoLine> Info =>
    [
        new("Profile", _session.Profile),
        new("Region", _session.Region),
        new("Source", "CloudWatch " + CloudGpuApi.Namespace),
        new("Refresh", $"every {_interval.TotalSeconds:0} s"),
        new("Jobs in progress", _jobs.Count.ToString(CultureInfo.CurrentCulture)),
    ];

    private ICloudGpuApi Api => _api ??= _connect(_session);

    public async IAsyncEnumerable<IReadOnlyList<PanelEntry>> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _timer ??= RefreshLoopAsync();
        List<PanelEntry> entries;
        try
        {
            if (_job is null)
            {
                _jobs = await Api.RunningJobsAsync(cancellationToken).ConfigureAwait(false);
                entries = [.. _jobs.Select(JobEntry)];
            }
            else
            {
                _metrics = await Api.MetricsAsync(_job, Minutes, _clock(), cancellationToken).ConfigureAwait(false);
                entries = [.. _metrics.Select(HostEntry)];
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not PluginException)
        {
            throw AwsErrors.Describe(ex, _job is null ? "Listing the training jobs in progress" : $"Reading the metrics of {_job.Name}", _session);
        }

        yield return entries;
    }

    public Task<bool> EnterAsync(PanelEntry entry, CancellationToken cancellationToken)
    {
        if (_job is null && entry.Tag is CloudJob job)
        {
            _job = job;
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<bool> GoUpAsync(CancellationToken cancellationToken)
    {
        if (_job is null)
        {
            return Task.FromResult(false);
        }

        _job = null;
        return Task.FromResult(true);
    }

    public Task<string?> QuickViewAsync(PanelEntry entry, CancellationToken cancellationToken) => Task.FromResult(Describe(entry));

    public Task<(string Title, string Text)?> ViewAsync(PanelEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult<(string Title, string Text)?>(Describe(entry) is { } text ? (entry.Name, text) : null);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_timer is not null)
        {
            try
            {
                await _timer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
        }

        _stop.Dispose();
        _api?.Dispose();
    }

    internal static string? Describe(PanelEntry entry)
    {
        if (entry.Tag is CloudJob job)
        {
            return $"{job.Name}\n\nInstances     {job.InstanceCount} × {job.InstanceType}\nStarted       {job.Started?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "not yet"}\n\nEnter lists the hosts with their GPU load.\n";
        }

        if (entry.Tag is not HostMetrics host)
        {
            return null;
        }

        // GPU values are summed over the host's GPUs: scale the charts to the largest value seen, at least 100.
        var gpuMax = Math.Max(100, host.Gpu.Concat(host.GpuMemory).Max(v => v ?? 0));
        var text = new StringBuilder();
        text.AppendLine(host.Host).AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Last {Minutes} minutes, one value per minute:");
        text.AppendLine(CultureInfo.InvariantCulture, $"GPU %       {TextChart.Sparkline(host.Gpu, gpuMax)}  now {TextChart.Value(HostMetrics.Last(host.Gpu), "%")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"GPU mem %   {TextChart.Sparkline(host.GpuMemory, gpuMax)}  now {TextChart.Value(HostMetrics.Last(host.GpuMemory), "%")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"CPU %       {TextChart.Sparkline(host.Cpu, Math.Max(100, host.Cpu.Max(v => v ?? 0)))}  now {TextChart.Value(HostMetrics.Last(host.Cpu), "%")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Mem %       {TextChart.Sparkline(host.Memory)}  now {TextChart.Value(HostMetrics.Last(host.Memory), "%")}");
        text.AppendLine();
        text.AppendLine("GPU and CPU values are summed over the host's GPUs and cores (4 GPUs at full load: 400%).");
        text.AppendLine("CloudWatch publishes them about a minute late.");
        return text.ToString();
    }

    private static PanelEntry JobEntry(CloudJob job) => new(job.Name, IsFolder: true)
    {
        Tag = job,
        Icon = PanelIcons.Gpu,
        Modified = job.Started,
        Cells = new Dictionary<string, CellValue> { ["instances"] = $"{job.InstanceCount} × {job.InstanceType}" },
    };

    private static PanelEntry HostEntry(HostMetrics host) => new(host.Host[(host.Host.LastIndexOf('/') + 1)..])
    {
        Tag = host,
        Icon = PanelIcons.Gpu,
        Tone = HostMetrics.Last(host.Gpu) is null ? RowTone.Dim : RowTone.Normal,
        Cells = new Dictionary<string, CellValue>
        {
            [LocalGpuPanel.LoadKey] = new(TextChart.Value(HostMetrics.Last(host.Gpu), "%"), HostMetrics.Last(host.Gpu)),
            [LocalGpuPanel.MemoryKey] = new(TextChart.Value(HostMetrics.Last(host.GpuMemory), "%"), HostMetrics.Last(host.GpuMemory)),
            ["cpu"] = new(TextChart.Value(HostMetrics.Last(host.Cpu), "%"), HostMetrics.Last(host.Cpu)),
            ["ram"] = new(TextChart.Value(HostMetrics.Last(host.Memory), "%"), HostMetrics.Last(host.Memory)),
        },
    };

    private async Task RefreshLoopAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
        {
            _host.Refresh(this);
        }
    }
}
