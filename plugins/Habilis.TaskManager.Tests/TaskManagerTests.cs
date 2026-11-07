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
        Assert.Null(entries[0].IconPath);
        Assert.Equal(@"C:\Apps\worker.exe", entries[1].IconPath);
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

        var pause = panel.Commands.Single(command => command.Key == "Shift+F4");
        await pause.Run(new PanelCommandContext(null, [], new FakeProgress(), CancellationToken.None));
        Assert.True(panel.Paused);
        Assert.Contains(host.Messages, message => message.Item2.Contains("paused", StringComparison.OrdinalIgnoreCase));

        await pause.Run(new PanelCommandContext(null, [], new FakeProgress(), CancellationToken.None));
        Assert.False(panel.Paused);
    }

    [Fact]
    public async Task F8_warns_before_ending_the_selected_process()
    {
        var host = new FakeHost();
        var source = new FakeSource();
        var plugin = new TaskManagerPlugin { Source = () => source };
        plugin.Initialize(host);
        await using var panel = (ProcessPanel)(await plugin.OpenAddressAsync("tasks:", CancellationToken.None))!;
        var entries = await Listing.AllAsync(panel);
        var end = panel.Commands.Single(command => command.Key == "F8");

        await end.Run(new PanelCommandContext(entries[1], [entries[1]], new FakeProgress(), CancellationToken.None));
        Assert.Empty(source.Ended);
        Assert.Contains("Unsaved data", Assert.Single(host.Confirmations).Message, StringComparison.Ordinal);

        host.ConfirmAnswers.Enqueue(true);
        var progress = new FakeProgress();
        await end.Run(new PanelCommandContext(entries[1], [entries[1]], progress, CancellationToken.None));

        Assert.Equal([42], source.Ended);
        Assert.Equal((1, null), progress.Totals);
        Assert.Equal(1, progress.Items);
    }

    private sealed class FakeSource : IProcessSource
    {
        public int Samples { get; private set; }

        public List<int> Ended { get; } = [];

        public ProcessSnapshot Sample()
        {
            Samples++;
            return new ProcessSnapshot(Now.AddSeconds(Samples),
            [
                new ProcessData(7, "idle.exe", "Suspended", null, 0, TimeSpan.FromSeconds(1), 8L << 20, null, 1, null, 0, "x64", Now.AddHours(-2), null, null),
                new ProcessData(42, "worker.exe", "Running", "DOMAIN\\sergey", 18.5, TimeSpan.FromMinutes(3), 2L << 30, 1500L << 20, 12, 130, 1, "x64", Now.AddMinutes(-5), @"C:\Apps\worker.exe", "worker.exe --train"),
            ]);
        }

        public bool End(ProcessData expected)
        {
            Ended.Add(expected.Pid);
            return true;
        }
    }
}
