using HabilisTerminal.Plugins;

namespace Habilis.TaskManager;

/// <summary>A live process list modelled on Windows Task Manager's Details page (issue #182).</summary>
public sealed class TaskManagerPlugin : Plugin
{
    internal Func<IProcessSource> Source { get; set; } = () => new WindowsProcessSource();

    internal TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(1);

    public override IEnumerable<PluginMenuItem> MenuItems =>
    [
        new("Task Manager · Details", _ => Open()),
    ];

    public override IEnumerable<PluginMenuItem> DriveMenuItems => MenuItems;

    public override IEnumerable<string> Prefixes => ["tasks"];

    public override Task<IPanelProvider?> OpenAddressAsync(string address, CancellationToken cancellationToken) => Open();

    private Task<IPanelProvider?> Open() => Task.FromResult<IPanelProvider?>(new ProcessPanel(Host, Source(), RefreshInterval));
}
