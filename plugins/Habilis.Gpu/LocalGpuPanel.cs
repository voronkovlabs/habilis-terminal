using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using HabilisTerminal.Plugins;
using Avalonia.Controls;
using HabilisTerminal.Plugins.Avalonia;

namespace Habilis.Gpu;

/// <summary>
/// This computer's NVIDIA GPUs, like nvtop (issues #70, #79). The panel draws itself (<see cref="NvtopView"/>): per GPU
/// load and memory, CPU and RAM, a chart of the recent values and the processes using the GPU. It samples every few
/// seconds; Shift+F4 pauses. The listing behind it (GPUs, then processes) feeds Ctrl+L, Ctrl+Q and F3. Read-only.
/// </summary>
internal sealed class LocalGpuPanel : IPanelProvider, IQuickViewEntry, IViewEntry, IPanelCommands, IPanelView
{
    public const string LoadKey = "load";
    public const string MemoryKey = "memory";
    public const string TemperatureKey = "temp";
    public const string PowerKey = "power";
    public const string GpuKey = "gpu";
    public const string PidKey = "pid";
    private const int HistoryLength = 120;

    private static readonly PanelColumn Load = new(LoadKey, "GPU %", 64, AlignRight: true);
    private static readonly PanelColumn Memory = new(MemoryKey, "Memory", 120, AlignRight: true);
    private static readonly PanelColumn Temperature = new(TemperatureKey, "Temp", 56, AlignRight: true);
    private static readonly PanelColumn Power = new(PowerKey, "Power", 96, AlignRight: true);
    private static readonly PanelColumn Gpu = new(GpuKey, "GPU", 40, AlignRight: true);
    private static readonly PanelColumn Pid = new(PidKey, "PID", 64, AlignRight: true);

    private readonly IPluginHost _host;
    private readonly IGpuSource _source;
    private readonly ISystemSource _system;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sampling = new(1, 1);
    private readonly Dictionary<string, (History Load, History Memory, History Temperature, History Power)> _history = [];
    private readonly History _cpu = new(HistoryLength);
    private readonly History _ram = new(HistoryLength);
    private GpuSnapshot? _snapshot;
    private LocalSample? _sample;
    private string? _gpu; // the GPU whose processes are listed, by uuid
    private Task? _timer;
    private NvtopView? _view;

    public LocalGpuPanel(IPluginHost host, IGpuSource source, ISystemSource system, TimeSpan interval)
    {
        _host = host;
        _source = source;
        _system = system;
        _interval = interval;
        Commands = [new PanelCommand("Pause or resume refreshing", "Shift+F4", _ => TogglePause())];
    }

    public string Title => "GPU monitor · this computer";

    public string Location => _gpu is null ? "gpu:" : $"gpu:{Selected?.Index}";

    public IReadOnlyList<PanelLayout> Layouts { get; } =
    [
        new("Monitor", [Load, Memory, Temperature, Power, Gpu, Pid]),
        new("Brief", [Load, Memory]),
    ];

    public IReadOnlyList<PanelSortKey> SortKeys { get; } = [new(LoadKey, "GPU load"), new(MemoryKey, "Memory")];

    public IReadOnlyList<PanelCommand> Commands { get; }

    public bool Paused { get; private set; }

    public IReadOnlyList<InfoLine> Info
    {
        get
        {
            var lines = new List<InfoLine> { new("Source", "nvidia-smi (NVIDIA driver)"), new("Refresh", Paused ? "paused" : $"every {_interval.TotalSeconds:0} s") };
            if (_snapshot is { } snapshot)
            {
                lines.Add(new("GPUs", snapshot.Gpus.Count.ToString(CultureInfo.CurrentCulture)));
                lines.Add(new("Processes", snapshot.Processes.Count.ToString(CultureInfo.CurrentCulture)));
                lines.Add(new("Sampled at", snapshot.TakenUtc.ToLocalTime().ToString("T", CultureInfo.CurrentCulture)));
            }

            return lines;
        }
    }

    /// <summary>The latest sample (tests).</summary>
    internal LocalSample? LastSample => _sample;

    private GpuSample? Selected => _snapshot?.Gpus.FirstOrDefault(g => g.Uuid == _gpu);

    public async IAsyncEnumerable<IReadOnlyList<PanelEntry>> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The refresh loop samples just before it asks the host to list: a fresh sample is used, not taken again.
        if (_sample is null || DateTime.UtcNow - _sample.Gpus.TakenUtc > _interval / 2 || _sample.Gpus.TakenUtc > DateTime.UtcNow.AddMinutes(1))
        {
            await SampleAsync(cancellationToken).ConfigureAwait(false);
        }

        var snapshot = _snapshot!;
        _timer ??= RefreshLoopAsync();
        if (_gpu is not null && snapshot.Gpus.All(g => g.Uuid != _gpu))
        {
            _gpu = null; // the GPU went away
        }

        var entries = new List<PanelEntry>();
        if (_gpu is null)
        {
            entries.AddRange(snapshot.Gpus.Select(GpuEntry));
        }

        entries.AddRange(snapshot.Processes.Where(p => _gpu is null || p.GpuUuid == _gpu).Select(p => ProcessEntry(p, snapshot)));
        yield return entries;
    }

    public Task<bool> EnterAsync(PanelEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Tag is GpuSample gpu && _gpu is null)
        {
            _gpu = gpu.Uuid;
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<bool> GoUpAsync(CancellationToken cancellationToken)
    {
        if (_gpu is null)
        {
            return Task.FromResult(false);
        }

        _gpu = null;
        return Task.FromResult(true);
    }

    public Control CreateView(IPanelViewHost host)
    {
        _view = new NvtopView();
        ShowInView();
        return _view;
    }

    public Task<string?> QuickViewAsync(PanelEntry entry, CancellationToken cancellationToken) => Task.FromResult<string?>(Describe(entry));

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
        _sampling.Dispose();
    }

    internal static string GpuName(GpuSample gpu) => $"{gpu.Index}: {gpu.Name}";

    /// <summary>The quick view and F3 text: bars, charts of the last samples and the details.</summary>
    internal string? Describe(PanelEntry entry)
    {
        if (entry.Tag is GpuSample gpu && _snapshot is { } snapshot)
        {
            var history = _history.TryGetValue(gpu.Uuid, out var h) ? h : default;
            var text = new StringBuilder();
            text.AppendLine(GpuName(gpu)).AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"GPU     {TextChart.Bar(gpu.Utilization)}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Memory  {TextChart.Bar(gpu.MemoryPercent)}  {TextChart.Value(gpu.MemoryUsedMiB, " MiB")} of {TextChart.Value(gpu.MemoryTotalMiB, " MiB")}");
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"Last {HistoryLength} samples, one per {_interval.TotalSeconds:0} s:");
            if (history.Load is not null)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"GPU %   {TextChart.Sparkline(history.Load.Values)}");
                text.AppendLine(CultureInfo.InvariantCulture, $"Mem %   {TextChart.Sparkline(history.Memory.Values)}");
                text.AppendLine(CultureInfo.InvariantCulture, $"Temp    {TextChart.Sparkline(history.Temperature.Values, 100)}  (0–100 °C)");
                text.AppendLine(CultureInfo.InvariantCulture, $"Power   {TextChart.Sparkline(history.Power.Values, gpu.PowerLimitW ?? 300)}  (0–{TextChart.Value(gpu.PowerLimitW ?? 300, " W")})");
            }

            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"Temperature   {TextChart.Value(gpu.TemperatureC, " °C")}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Power         {TextChart.Value(gpu.PowerW, " W", "0.0")} of {TextChart.Value(gpu.PowerLimitW, " W")}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Fan           {TextChart.Value(gpu.FanPercent, "%")}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Memory load   {TextChart.Value(gpu.MemoryUtilization, "%")} (memory controller)");
            text.AppendLine(CultureInfo.InvariantCulture, $"UUID          {gpu.Uuid}");
            var processes = snapshot.Processes.Where(p => p.GpuUuid == gpu.Uuid).ToList();
            text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Processes ({processes.Count}):");
            foreach (var process in processes)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {process.Pid,7}  {TextChart.Value(process.MemoryMiB, " MiB"),10}  {process.Name}");
            }

            return text.ToString();
        }

        if (entry.Tag is GpuProcess p)
        {
            var on = _snapshot?.Gpus.FirstOrDefault(g => g.Uuid == p.GpuUuid);
            return $"{p.Name}\n\nPID           {p.Pid}\nGPU           {(on is null ? p.GpuUuid : GpuName(on))}\nGPU memory    {TextChart.Value(p.MemoryMiB, " MiB")}\n";
        }

        return null;
    }

    private static PanelEntry GpuEntry(GpuSample gpu) => new(GpuName(gpu), IsFolder: true)
    {
        Icon = PanelIcons.Gpu,
        Group = 0,
        Tag = gpu,
        Tone = gpu.Utilization >= 90 ? RowTone.Warning : gpu.Utilization >= 5 ? RowTone.Accent : RowTone.Normal,
        Cells = new Dictionary<string, CellValue>
        {
            [LoadKey] = new(TextChart.Value(gpu.Utilization, "%"), gpu.Utilization),
            [MemoryKey] = new($"{TextChart.Value(gpu.MemoryUsedMiB, "")} / {TextChart.Value(gpu.MemoryTotalMiB, " MiB")}", gpu.MemoryUsedMiB),
            [TemperatureKey] = new(TextChart.Value(gpu.TemperatureC, " °C"), gpu.TemperatureC),
            [PowerKey] = new($"{TextChart.Value(gpu.PowerW, "", "0")} / {TextChart.Value(gpu.PowerLimitW, " W")}", gpu.PowerW),
            [GpuKey] = new(gpu.Index.ToString(CultureInfo.InvariantCulture), gpu.Index),
        },
    };

    private static PanelEntry ProcessEntry(GpuProcess process, GpuSnapshot snapshot)
    {
        var gpu = snapshot.Gpus.FirstOrDefault(g => g.Uuid == process.GpuUuid);
        return new PanelEntry($"{Path.GetFileName(process.Name)} ({process.Pid})")
        {
            Icon = PanelIcons.Process,
            Group = 1,
            Tag = process,
            Cells = new Dictionary<string, CellValue>
            {
                [MemoryKey] = new(TextChart.Value(process.MemoryMiB, " MiB"), process.MemoryMiB),
                [GpuKey] = new(gpu?.Index.ToString(CultureInfo.InvariantCulture) ?? "?", gpu?.Index),
                [PidKey] = new(process.Pid.ToString(CultureInfo.InvariantCulture), process.Pid),
            },
        };
    }

    /// <summary>One sample of the GPUs, the system and the processes; recorded for the charts and shown.</summary>
    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        await _sampling.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var gpus = await _source.SampleAsync(cancellationToken).ConfigureAwait(false);
            var (system, processes) = await Task.Run(() => _system.Sample(gpus.Processes), cancellationToken).ConfigureAwait(false);
            Record(gpus);
            _cpu.Add(system.CpuPercent);
            _ram.Add(system.RamPercent);
            _sample = new LocalSample(gpus, system, processes);
            ShowInView();
        }
        finally
        {
            _sampling.Release();
        }
    }

    private void ShowInView()
    {
        if (_view is not { } view || _sample is not { } sample)
        {
            return;
        }

        var charts = _history.ToDictionary(h => h.Key, h => (h.Value.Load.Values, h.Value.Memory.Values));
        var cpu = _cpu.Values;
        var ram = _ram.Values;
        var paused = Paused;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => view.Show(sample, charts, cpu, ram, paused));
    }

    private void Record(GpuSnapshot snapshot)
    {
        _snapshot = snapshot;
        foreach (var gpu in snapshot.Gpus)
        {
            if (!_history.TryGetValue(gpu.Uuid, out var history))
            {
                history = (new History(HistoryLength), new History(HistoryLength), new History(HistoryLength), new History(HistoryLength));
                _history[gpu.Uuid] = history;
            }

            history.Load.Add(gpu.Utilization);
            history.Memory.Add(gpu.MemoryPercent);
            history.Temperature.Add(gpu.TemperatureC);
            history.Power.Add(gpu.PowerW);
        }
    }

    private Task TogglePause()
    {
        Paused = !Paused;
        _host.ShowStatus(Paused ? "GPU monitor paused (Shift+F4 resumes)." : "GPU monitor refreshing again.");
        ShowInView();
        return Task.CompletedTask;
    }

    private async Task RefreshLoopAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
        {
            if (!Paused)
            {
                try
                {
                    await SampleAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (PluginException)
                {
                    // nvidia-smi failed this once: the listing shows the error.
                }

                _host.Refresh(this);
            }
        }
    }
}
