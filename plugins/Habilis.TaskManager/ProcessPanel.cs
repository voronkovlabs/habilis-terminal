using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using HabilisTerminal.Plugins;

namespace Habilis.TaskManager;

/// <summary>A read-only, live Details table. The host draws and sorts it and preserves its cursor on refresh.</summary>
internal sealed class ProcessPanel : IPanelProvider, IQuickViewEntry, IViewEntry, IPanelCommands
{
    internal const string PidKey = "pid";
    internal const string StatusKey = "status";
    internal const string UserKey = "user";
    internal const string CpuKey = "cpu";
    internal const string MemoryKey = "memory";
    internal const string PrivateKey = "private";
    internal const string ArchitectureKey = "architecture";
    internal const string ThreadsKey = "threads";
    internal const string HandlesKey = "handles";
    internal const string SessionKey = "session";
    internal const string CpuTimeKey = "cpu-time";
    internal const string StartedKey = "started";
    internal const string CommandKey = "command";

    private static readonly PanelColumn Pid = new(PidKey, "PID", 52, AlignRight: true);
    private static readonly PanelColumn Status = new(StatusKey, "Status", 68);
    private static readonly PanelColumn User = new(UserKey, "User name", 108);
    private static readonly PanelColumn Cpu = new(CpuKey, "CPU", 48, AlignRight: true);
    private static readonly PanelColumn Memory = new(MemoryKey, "Memory", 76, AlignRight: true);
    private static readonly PanelColumn Private = new(PrivateKey, "Private", 76, AlignRight: true);
    private static readonly PanelColumn Architecture = new(ArchitectureKey, "Arch", 60);
    private static readonly PanelColumn Threads = new(ThreadsKey, "Threads", 56, AlignRight: true);
    private static readonly PanelColumn Handles = new(HandlesKey, "Handles", 58, AlignRight: true);
    private static readonly PanelColumn Session = new(SessionKey, "Session", 54, AlignRight: true);
    private static readonly PanelColumn CpuTime = new(CpuTimeKey, "CPU time", 70, AlignRight: true);
    private static readonly PanelColumn Started = new(StartedKey, "Started", 105);
    private static readonly PanelColumn Command = new(CommandKey, "Command line", 120);

    private readonly IPluginHost _host;
    private readonly IProcessSource _source;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sampling = new(1, 1);
    private ProcessSnapshot? _snapshot;
    private Task? _timer;

    public ProcessPanel(IPluginHost host, IProcessSource source, TimeSpan interval)
    {
        _host = host;
        _source = source;
        _interval = interval;
        Commands = [new PanelCommand("Pause or resume refreshing", "Shift+F4", _ => TogglePause())];
    }

    public string Title => "Task Manager · Details";

    public string Location => "tasks:";

    public IReadOnlyList<PanelLayout> Layouts { get; } =
    [
        new("Details", [Pid, Status, User, Cpu, Memory, Architecture]),
        new("Performance", [Pid, Cpu, CpuTime, Memory, Private, Threads, Handles]),
        new("Identity", [Pid, User, Session, Architecture, Started, Command]),
    ];

    public IReadOnlyList<PanelSortKey> SortKeys { get; } =
    [
        new(PidKey, "PID"),
        new(StatusKey, "Status"),
        new(UserKey, "User name"),
        new(CpuKey, "CPU"),
        new(MemoryKey, "Memory"),
        new(PrivateKey, "Private memory"),
        new(ArchitectureKey, "Architecture"),
        new(ThreadsKey, "Threads"),
        new(HandlesKey, "Handles"),
        new(SessionKey, "Session"),
        new(CpuTimeKey, "CPU time"),
        new(StartedKey, "Started"),
        new(CommandKey, "Command line"),
    ];

    string? IPanelProvider.DefaultSortKey => CpuKey;

    bool IPanelProvider.DefaultSortDescending => true;

    public IReadOnlyList<PanelCommand> Commands { get; }

    public bool ShowsParent => false;

    public bool Paused { get; private set; }

    public IReadOnlyList<InfoLine> Info
    {
        get
        {
            var lines = new List<InfoLine>
            {
                new("View", "Windows processes"),
                new("Refresh", Paused ? "paused" : $"every {_interval.TotalSeconds:0.##} s"),
            };
            if (_snapshot is { } snapshot)
            {
                lines.Add(new("Processes", snapshot.Processes.Count.ToString(CultureInfo.CurrentCulture)));
                lines.Add(new("Suspended", snapshot.Processes.Count(p => p.Status == "Suspended").ToString(CultureInfo.CurrentCulture)));
                lines.Add(new("Sampled at", snapshot.TakenUtc.ToLocalTime().ToString("T", CultureInfo.CurrentCulture)));
            }

            return lines;
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<PanelEntry>> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_snapshot is null || DateTime.UtcNow - _snapshot.TakenUtc > _interval / 2)
        {
            await SampleAsync(cancellationToken).ConfigureAwait(false);
        }

        _timer ??= RefreshLoopAsync();
        yield return _snapshot!.Processes.Select(ToEntry).ToList();
    }

    public Task<bool> EnterAsync(PanelEntry entry, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<bool> GoUpAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<string?> QuickViewAsync(PanelEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult(entry.Tag is ProcessData process ? Describe(process) : null);

    public Task<(string Title, string Text)?> ViewAsync(PanelEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult<(string Title, string Text)?>(entry.Tag is ProcessData process ? ($"{process.Name} ({process.Pid})", Describe(process)) : null);

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

    private static PanelEntry ToEntry(ProcessData process) => new($"{process.Name} ({process.Pid})")
    {
        Icon = PanelIcons.Process,
        Tag = process,
        Tone = process.Status == "Suspended" ? RowTone.Dim : process.CpuPercent >= 50 ? RowTone.Warning : process.CpuPercent >= 10 ? RowTone.Accent : RowTone.Normal,
        Cells = new Dictionary<string, CellValue>
        {
            [PidKey] = new(process.Pid.ToString(CultureInfo.InvariantCulture), process.Pid),
            [StatusKey] = new(process.Status, process.Status),
            [UserKey] = Cell(process.User),
            [CpuKey] = Number(process.CpuPercent, "%", "0.0"),
            [MemoryKey] = Bytes(process.WorkingSet),
            [PrivateKey] = Bytes(process.PrivateBytes),
            [ArchitectureKey] = Cell(process.Architecture),
            [ThreadsKey] = Number(process.Threads),
            [HandlesKey] = Number(process.Handles),
            [SessionKey] = Number(process.SessionId),
            [CpuTimeKey] = new(FormatDuration(process.CpuTime), process.CpuTime),
            [StartedKey] = new(process.Started?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "", process.Started),
            [CommandKey] = Cell(process.CommandLine),
        },
    };

    private static CellValue Cell(string? value) => new(value ?? "", value);

    private static CellValue Number(double? value, string suffix, string format) =>
        new(value is { } number ? number.ToString(format, CultureInfo.CurrentCulture) + suffix : "", value);

    private static CellValue Number(int? value) =>
        new(value?.ToString(CultureInfo.CurrentCulture) ?? "", value);

    private static CellValue Bytes(long? value) =>
        new(value is { } bytes ? FormatBytes(bytes) : "", value);

    private static string FormatBytes(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
        : (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.CurrentCulture) + " MB";

    private static string FormatDuration(TimeSpan? value) => value is { } time
        ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
        : "";

    private static string Describe(ProcessData p)
    {
        var text = new StringBuilder();
        text.AppendLine(p.Name).AppendLine();
        text.AppendLine(CultureInfo.CurrentCulture, $"PID              {p.Pid}");
        text.AppendLine(CultureInfo.CurrentCulture, $"Status           {p.Status}");
        text.AppendLine(CultureInfo.CurrentCulture, $"User             {p.User ?? "not available"}");
        text.AppendLine(CultureInfo.CurrentCulture, $"Architecture     {p.Architecture ?? "not available"}");
        text.AppendLine(CultureInfo.CurrentCulture, $"Session          {p.SessionId?.ToString(CultureInfo.CurrentCulture) ?? "not available"}");
        text.AppendLine(CultureInfo.CurrentCulture, $"CPU              {(p.CpuPercent is { } cpu ? cpu.ToString("0.0", CultureInfo.CurrentCulture) + "%" : "sampling")}");
        text.AppendLine(CultureInfo.CurrentCulture, $"CPU time         {FormatDuration(p.CpuTime)}");
        text.AppendLine(CultureInfo.CurrentCulture, $"Memory           {(p.WorkingSet is { } memory ? FormatBytes(memory) : "not available")}");
        text.AppendLine(CultureInfo.CurrentCulture, $"Private memory   {(p.PrivateBytes is { } privateBytes ? FormatBytes(privateBytes) : "not available")}");
        text.AppendLine(CultureInfo.CurrentCulture, $"Threads          {p.Threads?.ToString(CultureInfo.CurrentCulture) ?? "not available"}");
        text.AppendLine(CultureInfo.CurrentCulture, $"Handles          {p.Handles?.ToString(CultureInfo.CurrentCulture) ?? "not available"}");
        text.AppendLine(CultureInfo.CurrentCulture, $"Started          {p.Started?.ToLocalTime().ToString("F", CultureInfo.CurrentCulture) ?? "not available"}");
        text.AppendLine().AppendLine("Command line").AppendLine(p.CommandLine ?? "not available");
        return text.ToString();
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        await _sampling.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _snapshot = await Task.Run(_source.Sample, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sampling.Release();
        }
    }

    private Task TogglePause()
    {
        Paused = !Paused;
        _host.ShowStatus(Paused ? "Task Manager paused (Shift+F4 resumes)." : "Task Manager refreshing again.");
        return Task.CompletedTask;
    }

    private async Task RefreshLoopAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
        {
            if (Paused)
            {
                continue;
            }

            await SampleAsync(_stop.Token).ConfigureAwait(false);
            _host.Refresh(this);
        }
    }
}
