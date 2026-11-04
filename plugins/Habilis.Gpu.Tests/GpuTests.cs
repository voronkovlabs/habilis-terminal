using Habilis.Aws.Common;
using Habilis.Aws.Tests;
using Habilis.Gpu;
using HabilisTerminal.Plugins;

namespace Habilis.Gpu.Tests;

public sealed class GpuTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private const string TwoGpus = """
        0, GPU-aaa, NVIDIA A10G, 87, 40, 18000, 23028, 71, 210.5, 300.00, 55
        1, GPU-bbb, NVIDIA A10G, 3, 0, 500, 23028, 40, 30.12, 300.00, [N/A]
        """;

    private const string Processes = """
        GPU-aaa, 4242, C:\train\python.exe, 17000
        GPU-bbb, 77, C:\Program Files\App, Inc\app.exe, [N/A]
        """;

    [Fact]
    public void Nvidia_smi_csv_parses_with_missing_values_and_commas_in_process_names()
    {
        var gpus = NvidiaSmiFormat.ParseGpus(TwoGpus);
        var processes = NvidiaSmiFormat.ParseProcesses(Processes + "\nNo running processes found");

        Assert.Equal(2, gpus.Count);
        Assert.Equal(("GPU-aaa", 87.0, 18000.0, 71.0, 210.5, 55.0), (gpus[0].Uuid, gpus[0].Utilization, gpus[0].MemoryUsedMiB, gpus[0].TemperatureC, gpus[0].PowerW, gpus[0].FanPercent));
        Assert.Null(gpus[1].FanPercent);
        Assert.Equal(78.2, Math.Round(gpus[0].MemoryPercent!.Value, 1));
        Assert.Equal(@"C:\Program Files\App, Inc\app.exe", processes[1].Name);
        Assert.Null(processes[1].MemoryMiB);
        Assert.Empty(NvidiaSmiFormat.ParseGpus("garbage\n"));
    }

    [Fact]
    public void Text_charts_scale_values_and_leave_gaps_for_missing_ones()
    {
        Assert.Equal("▁▅█ █", TextChart.Sparkline([0, 50, 100, null, 250]));
        Assert.Equal("[█████░░░░░] 52%", TextChart.Bar(52, 10));
        Assert.Equal("[          ] n/a", TextChart.Bar(null, 10));
    }

    [Fact]
    public async Task The_local_panel_lists_gpus_then_processes_enters_a_gpu_and_refreshes_itself()
    {
        var host = new FakeHost();
        var source = new FakeSource();
        var plugin = new GpuPlugin { Local = () => source, System = () => new FakeSystem(), Clock = () => Now };
        plugin.Initialize(host);
        host.Save(new System.Text.Json.Nodes.JsonObject { ["localIntervalSeconds"] = 1 });
        await using var panel = (LocalGpuPanel)(await plugin.OpenAddressAsync("gpu:", CancellationToken.None))!;

        var entries = await Listing.AllAsync(panel);
        Assert.Equal(["0: NVIDIA A10G", "1: NVIDIA A10G", "python.exe (4242)", "app.exe (77)"], entries.Select(e => e.Name));
        Assert.All(entries.Take(2), e => Assert.Equal((PanelIcons.Gpu, true, 0), (e.Icon, e.IsFolder, e.Group)));
        Assert.Equal(PanelIcons.Process, entries[2].Icon);
        Assert.Equal("87%", entries[0].Cells![LocalGpuPanel.LoadKey].Text);
        Assert.Equal(RowTone.Accent, entries[0].Tone); // busy, below the 90% warning

        Assert.True(await panel.EnterAsync(entries[0], CancellationToken.None));
        Assert.Equal("gpu:0", panel.Location);
        Assert.Equal(["python.exe (4242)"], (await Listing.AllAsync(panel)).Select(e => e.Name));
        Assert.True(await panel.GoUpAsync(CancellationToken.None));
        Assert.False(await panel.GoUpAsync(CancellationToken.None));

        var view = await panel.QuickViewAsync(entries[0], CancellationToken.None);
        Assert.Contains("GPU     [", view, StringComparison.Ordinal);
        Assert.Contains("GPU %   ▇▇", view, StringComparison.Ordinal); // two samples at 87%
        Assert.Contains("4242", view, StringComparison.Ordinal);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (host.Refreshed.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Contains(panel, host.Refreshed);
    }

    [Fact]
    public async Task Without_nvidia_smi_the_panel_says_so()
    {
        var host = new FakeHost();
        var plugin = new GpuPlugin { System = () => new FakeSystem(), Local = () => new FakeSource { Failure = new PluginException("nvidia-smi was not found: the GPU monitor needs an NVIDIA GPU and its driver.") } };
        plugin.Initialize(host);
        await using var panel = (await plugin.OpenAddressAsync("gpu:", CancellationToken.None))!;

        var error = await Assert.ThrowsAsync<PluginException>(() => Listing.AllAsync(panel));
        Assert.Contains("nvidia-smi was not found", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sagemaker_view_lists_running_jobs_and_their_hosts_with_charts()
    {
        var host = new FakeHost();
        var cloud = new FakeCloud();
        var plugin = new GpuPlugin { Cloud = _ => cloud, Clock = () => Now };
        plugin.Initialize(host);
        await using var panel = (CloudGpuPanel)(await plugin.OpenAddressAsync("gpu:sm:prod/eu-west-1", CancellationToken.None))!;

        Assert.Equal("gpu:sm:prod/eu-west-1", panel.Location);
        var jobs = await Listing.AllAsync(panel);
        Assert.Equal(["train-1"], jobs.Select(j => j.Name));
        Assert.Equal("2 × ml.g5.12xlarge", jobs[0].Cells!["instances"].Text);
        Assert.Equal("380%", jobs[0].Cells![LocalGpuPanel.LoadKey].Text); // the numbers are on the job's row too (issue #99)
        Assert.Equal("250%", jobs[0].Cells![LocalGpuPanel.MemoryKey].Text);
        Assert.Equal("900%", jobs[0].Cells!["cpu"].Text);
        Assert.Equal("40%", jobs[0].Cells!["ram"].Text);

        Assert.True(await panel.EnterAsync(jobs[0], CancellationToken.None));
        var hosts = await Listing.AllAsync(panel);
        Assert.Equal(["algo-1", "algo-2"], hosts.Select(h => h.Name));
        Assert.Equal("380%", hosts[0].Cells![LocalGpuPanel.LoadKey].Text);
        Assert.Equal(RowTone.Dim, hosts[1].Tone); // no data yet
        Assert.Equal(("train-1", 30, Now), cloud.Asked);

        var view = await panel.QuickViewAsync(hosts[0], CancellationToken.None);
        Assert.Contains("now 380%", view, StringComparison.Ordinal);
        Assert.Contains("summed over the host's GPUs", view, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_cloudwatch_the_job_list_still_works_and_says_why_there_are_no_numbers()
    {
        var host = new FakeHost();
        var cloud = new FakeCloud { MetricsFailure = new Amazon.CloudWatch.AmazonCloudWatchException("User is not authorized") { ErrorCode = "AccessDenied" } };
        var plugin = new GpuPlugin { Cloud = _ => cloud, Clock = () => Now };
        plugin.Initialize(host);
        await using var panel = (CloudGpuPanel)(await plugin.OpenAddressAsync("gpu:sm:prod/eu-west-1", CancellationToken.None))!;

        var jobs = await Listing.AllAsync(panel);

        Assert.Equal(["train-1"], jobs.Select(j => j.Name));
        Assert.Equal("n/a", jobs[0].Cells![LocalGpuPanel.LoadKey].Text);
        Assert.Contains(host.Messages, m => m.Item2.Contains("metrics", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_cloud_error_is_described_in_plain_words()
    {
        var host = new FakeHost();
        var cloud = new FakeCloud { Failure = new Amazon.SageMaker.AmazonSageMakerException("User is not authorized") { ErrorCode = "AccessDeniedException" } };
        var plugin = new GpuPlugin { Cloud = _ => cloud, Clock = () => Now };
        plugin.Initialize(host);
        await using var panel = (await plugin.OpenAddressAsync("gpu:sm:prod/eu-west-1", CancellationToken.None))!;

        var error = await Assert.ThrowsAsync<PluginException>(() => Listing.AllAsync(panel));
        Assert.Contains("training jobs in progress", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_nvtop_view_gets_cpu_ram_and_process_gpu_use_and_the_cloud_job_list_has_no_parent_row()
    {
        var host = new FakeHost();
        var plugin = new GpuPlugin { Local = () => new FakeSource(), System = () => new FakeSystem(), Clock = () => Now, Cloud = _ => new FakeCloud() };
        plugin.Initialize(host);
        await using var panel = (LocalGpuPanel)(await plugin.OpenAddressAsync("gpu:", CancellationToken.None))!;
        await Listing.AllAsync(panel);

        Assert.IsAssignableFrom<HabilisTerminal.Plugins.Wpf.IPanelView>(panel);
        var sample = panel.LastSample!;
        Assert.Equal(37, sample.System.CpuPercent);
        Assert.Equal(50, sample.System.RamPercent);
        Assert.Equal(("python", 81.0), (sample.Processes[0].Name, sample.Processes[0].GpuPercent!.Value));

        await using var cloud = (CloudGpuPanel)(await plugin.OpenAddressAsync("gpu:sm:prod/eu-west-1", CancellationToken.None))!;
        Assert.False(cloud.ShowsParent);
        var jobs = await Listing.AllAsync(cloud);
        await cloud.EnterAsync(jobs[0], CancellationToken.None);
        Assert.True(cloud.ShowsParent);
    }

    private sealed class FakeSystem : ISystemSource
    {
        public (SystemSample System, IReadOnlyList<ProcessUse> Processes) Sample(IReadOnlyList<GpuProcess> nvidiaProcesses) =>
            (new SystemSample(37, 8192, 16384), [new ProcessUse(4242, "python", "Compute", 81, 17000, 12, 2048)]);
    }

    private sealed class FakeSource : IGpuSource
    {
        public Exception? Failure { get; init; }

        public Task<GpuSnapshot> SampleAsync(CancellationToken cancellationToken) => Failure is { } failure
            ? Task.FromException<GpuSnapshot>(failure)
            : Task.FromResult(new GpuSnapshot(NvidiaSmiFormat.ParseGpus(TwoGpus), NvidiaSmiFormat.ParseProcesses(Processes), Now));
    }

    private sealed class FakeCloud : ICloudGpuApi
    {
        public Exception? MetricsFailure { get; init; }

        public Exception? Failure { get; init; }

        public (string Job, int Minutes, DateTime Now) Asked { get; private set; }

        public Task<IReadOnlyList<CloudJob>> RunningJobsAsync(CancellationToken cancellationToken) => Failure is { } failure
            ? Task.FromException<IReadOnlyList<CloudJob>>(failure)
            : Task.FromResult<IReadOnlyList<CloudJob>>([new CloudJob("train-1", "ml.g5.12xlarge", 2, Now.AddHours(-1))]);

        public Task<IReadOnlyList<HostMetrics>> MetricsAsync(CloudJob job, int minutes, DateTime now, CancellationToken cancellationToken)
        {
            if (MetricsFailure is { } failure)
            {
                return Task.FromException<IReadOnlyList<HostMetrics>>(failure);
            }

            Asked = (job.Name, minutes, now);
            IReadOnlyList<double?> Busy(double value) => [.. Enumerable.Repeat<double?>(null, minutes - 3), value - 20, value - 10, value];
            IReadOnlyList<double?> None() => [.. Enumerable.Repeat<double?>(null, minutes)];
            return Task.FromResult<IReadOnlyList<HostMetrics>>(
            [
                new HostMetrics($"{job.Name}/algo-1", Busy(380), Busy(250), Busy(900), Busy(40)),
                new HostMetrics($"{job.Name}/algo-2", None(), None(), None(), None()),
            ]);
        }

        public void Dispose()
        {
        }
    }
}
