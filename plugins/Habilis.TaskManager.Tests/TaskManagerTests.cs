using Habilis.Aws.Tests;
using Habilis.TaskManager;
using HabilisTerminal.Plugins;

namespace Habilis.TaskManager.Tests;

public sealed class TaskManagerTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Plugin_opens_a_live_details_panel_with_sortable_process_fields()
    {
        var host = new FakeHost();
        var source = new FakeSource();
        var plugin = new TaskManagerPlugin { Source = () => source, RefreshInterval = TimeSpan.FromMilliseconds(20) };
        plugin.Initialize(host);
        await using var panel = (ProcessPanel)(await plugin.OpenAddressAsync("tasks:", CancellationToken.None))!;

        var entries = await Listing.AllAsync(panel);

        Assert.False(panel.ShowsParent);
        Assert.Equal("tasks:", panel.Location);
        Assert.Equal('0', Assert.Single(plugin.MenuItems).DefaultHotkey);
        Assert.Equal(ProcessPanel.CpuKey, ((IPanelProvider)panel).DefaultSortKey);
        Assert.True(((IPanelProvider)panel).DefaultSortDescending);
        Assert.True(((IPanelProvider)panel).StartsWithoutCursor);
        Assert.True(((IPanelProvider)panel).FreezeOrderWhileCursorVisible);
        Assert.Equal(3, panel.Layouts.Count);
        Assert.Equal(["idle.exe (7)", "worker.exe (42)"], entries.Select(e => e.Name));
        Assert.All(entries, entry => Assert.Equal(PanelIcons.Process, entry.Icon));
        Assert.Equal("42", entries[1].Cells![ProcessPanel.PidKey].Text);
        Assert.Equal("18.5%", entries[1].Cells![ProcessPanel.CpuKey].Text);
        Assert.Equal("2.0 GB", entries[1].Cells![ProcessPanel.MemoryKey].Text);
        Assert.Equal(RowTone.Accent, entries[1].Tone);
        Assert.Equal(RowTone.Dim, entries[0].Tone);

        var detail = await panel.QuickViewAsync(entries[1], CancellationToken.None);
        Assert.Contains("DOMAIN\\sergey", detail, StringComparison.Ordinal);
        Assert.Contains("worker.exe --train", detail, StringComparison.Ordinal);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (host.Refreshed.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Contains(panel, host.Refreshed);
        Assert.True(source.Samples >= 2);
    }

    [Fact]
    public async Task Pause_command_stops_and_resumes_refreshing()
    {
        var host = new FakeHost();
        var plugin = new TaskManagerPlugin { Source = () => new FakeSource(), RefreshInterval = TimeSpan.FromMilliseconds(20) };
        plugin.Initialize(host);
        await using var panel = (ProcessPanel)(await plugin.OpenAddressAsync("tasks:", CancellationToken.None))!;
        await Listing.AllAsync(panel);

        await panel.Commands[0].Run(new PanelCommandContext(null, [], new FakeProgress(), CancellationToken.None));
        Assert.True(panel.Paused);
        Assert.Contains(host.Messages, message => message.Item2.Contains("paused", StringComparison.OrdinalIgnoreCase));

        await panel.Commands[0].Run(new PanelCommandContext(null, [], new FakeProgress(), CancellationToken.None));
        Assert.False(panel.Paused);
    }

    private sealed class FakeSource : IProcessSource
    {
        public int Samples { get; private set; }

        public ProcessSnapshot Sample()
        {
            Samples++;
            return new ProcessSnapshot(Now.AddSeconds(Samples),
            [
                new ProcessData(7, "idle.exe", "Suspended", null, 0, TimeSpan.FromSeconds(1), 8L << 20, null, 1, null, 0, "x64", Now.AddHours(-2), null),
                new ProcessData(42, "worker.exe", "Running", "DOMAIN\\sergey", 18.5, TimeSpan.FromMinutes(3), 2L << 30, 1500L << 20, 12, 130, 1, "x64", Now.AddMinutes(-5), "worker.exe --train"),
            ]);
        }
    }
}
