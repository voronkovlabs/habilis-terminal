using HabilisTerminal.Core.Diagnostics;

namespace HabilisTerminal.Core.Tests;

public class PerfTests
{
    private static PerfOperation Operation(string name, long count, double wall, double? cpu, double max, params (int Bucket, int Count)[] buckets)
    {
        var values = new int[Perf.BucketCount];
        foreach (var (bucket, amount) in buckets)
        {
            values[bucket] = amount;
        }

        return new PerfOperation(name, null, count, wall, cpu, max, values);
    }

    private static PerfPeriod Period(params PerfOperation[] operations) =>
        new(new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 5, 10, 5, 0, DateTimeKind.Utc), operations, [], 1000, 10);

    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(1, 0)]
    [InlineData(1.5, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(1e9, Perf.BucketCount - 1)]
    public void Durations_fall_into_doubling_buckets(double ms, int bucket) => Assert.Equal(bucket, Perf.BucketOf(ms));

    [Theory]
    [InlineData(90, 100, "computes")]
    [InlineData(70, 100, "computes")]
    [InlineData(50, 100, "mixed")]
    [InlineData(30, 100, "waits")]
    [InlineData(2, 100, "waits")]
    [InlineData(0, 5, "too short to tell")]
    public void Cpu_share_tells_computing_from_waiting(double cpu, double wall, string kind) => Assert.Equal(kind, PerfReport.Kind(cpu, wall));

    [Fact]
    public void Percentile_is_the_bucket_holding_it()
    {
        var operation = Operation("x", 100, 500, 100, 900, (1, 94), (5, 5), (10, 1));
        Assert.Equal(32, PerfReport.Percentile95(operation)); // the 95th of 100 is in the 5th bucket, up to 32 ms
    }

    [Fact]
    public void Percentile_is_not_above_the_longest()
    {
        var operation = Operation("x", 1, 3, 1, 3, (2, 1));
        Assert.Equal(3, PerfReport.Percentile95(operation));
    }

    [Fact]
    public void Periods_add_up()
    {
        var merged = Assert.Single(PerfReport.Merge([Period(Operation("x", 2, 10, 4, 7, (2, 2))), Period(Operation("x", 3, 20, null, 9, (2, 3)))]));
        Assert.Equal(5, merged.Count);
        Assert.Equal(30, merged.WallMs);
        Assert.Equal(4, merged.CpuMs);
        Assert.Equal(9, merged.MaxWallMs);
        Assert.Equal(5, merged.Buckets[2]);
    }

    [Fact]
    public void Cpu_of_threads_that_ended_is_the_difference_from_the_process()
    {
        var sampled = Period() with { Threads = [new PerfThread("UI thread", 300), new PerfThread("Folder sizes", 200)] };
        Assert.Equal(500, PerfReport.EndedThreadsCpuMs([sampled, Period()])); // a period with no thread data says nothing
        var draft = PerfReport.Build([sampled], "1.2.3", "Windows 11", 8);
        Assert.Contains("Threads that ended before their last sample", draft.Body);
    }

    [Fact]
    public void A_stall_with_no_operation_is_named_by_its_window_message()
    {
        var stall = new PerfStall(DateTime.UtcNow, 800, 40, 0, null, PerfReport.WindowMessagePrefix + "WM_TIMER");
        var draft = PerfReport.Build([Period() with { Stalls = [stall] }], "1.2.3", "Windows 11", 8);
        Assert.Contains("| window message WM_TIMER | 1 |", draft.Body);
    }

    [Fact]
    public void The_report_names_what_took_the_time()
    {
        var stall = new PerfStall(DateTime.UtcNow, 800, 40, 0, "panel.navigate", null);
        var period = Period(Operation("panel.render", 10, 200, 190, 50, (5, 10))) with { Stalls = [stall] };
        var draft = PerfReport.Build([period], "1.2.3", "Windows 11", 8);

        Assert.Equal("Performance report 1.2.3", draft.Title);
        Assert.Contains("performance", draft.Labels);
        Assert.Contains("| panel.navigate | 1 | 800 | 800 | 5% | waits |", draft.Body, StringComparison.Ordinal);
        Assert.Contains("panel.render", draft.Body, StringComparison.Ordinal);
        Assert.Contains("computes", draft.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_shows_cpu_by_thread_memory_and_the_longest_stalls()
    {
        var stall = new PerfStall(new DateTime(2026, 11, 5, 10, 1, 0, DateTimeKind.Utc), 1900, 19, 0, "panel.render.folder-size", null);
        var period = Period() with
        {
            Stalls = [stall],
            WorkingSetMb = 321,
            ManagedHeapMb = 45,
            Threads = [new PerfThread("Folder sizes", 20000), new PerfThread("unnamed, Microsoft.Terminal.Control.dll", 15000), new PerfThread("idle one", 0.2)],
        };
        var body = PerfReport.Build([period], "1", "W", 1).Body;

        Assert.Contains("| Folder sizes | 20.0 |", body, StringComparison.Ordinal);
        Assert.True(body.IndexOf("Folder sizes", StringComparison.Ordinal) < body.IndexOf("Microsoft.Terminal.Control", StringComparison.Ordinal));
        Assert.DoesNotContain("idle one", body, StringComparison.Ordinal);
        Assert.Contains("up to 321 MB", body, StringComparison.Ordinal);
        Assert.Contains("| 60 | 1900 | 1% | 0 | panel.render.folder-size | - |", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_says_what_the_ui_thread_waited_for_and_whether_the_machine_stood()
    {
        var at = new DateTime(2026, 11, 5, 10, 1, 0, DateTimeKind.Utc);
        var period = Period() with
        {
            Stalls =
            [
                new PerfStall(at, 3000, 0, 0, "panel.render.text", null, "wait: PageIn", 2500, 96),
                new PerfStall(at, 1000, 0, 0, "panel.render.text", null, "wait: PageIn", 700, 90),
                new PerfStall(at, 500, 450, 0, null, null, "running", 3, 40),
            ],
        };
        var body = PerfReport.Build([period], "1", "W", 1).Body;

        Assert.Contains("| wait: PageIn | 2 | 4000 | 3000 | 2500 | 96% |", body, StringComparison.Ordinal);
        Assert.Contains("| running | 1 | 500 | 500 | 3 | 40% |", body, StringComparison.Ordinal);
        Assert.Contains("| panel.render.text | - | wait: PageIn | 2500 | 96% |", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Stalls_saved_by_an_older_version_still_make_a_report()
    {
        var body = PerfReport.Build([Period() with { Stalls = [new PerfStall(DateTime.UtcNow, 200, 100, 0, "key", null)] }], "1", "W", 1).Body;

        Assert.DoesNotContain("as Windows saw it", body, StringComparison.Ordinal);
        Assert.Contains("| key | - | - | - | - |", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stall_with_nothing_named_says_so()
    {
        var period = Period() with { Stalls = [new PerfStall(DateTime.UtcNow, 200, 100, 0, null, null)] };
        Assert.Contains("window messages, nothing named", PerfReport.Build([period], "1", "W", 1).Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5, "under 100")]
    [InlineData(100, "100-1k")]
    [InlineData(6000, "1k-10k")]
    [InlineData(200000, "over 100k")]
    public void Counts_show_as_ranges(int count, string range) => Assert.Equal(range, Perf.CountRange(count));

    [Fact]
    public void An_operation_without_cpu_reads_as_asynchronous()
    {
        var draft = PerfReport.Build([Period(Operation("panel.navigate", 1, 100, null, 100, (7, 1)))], "1", "W", 1);
        Assert.Contains("asynchronous", draft.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_store_keeps_periods_until_their_files_are_deleted()
    {
        var folder = Path.Combine(Path.GetTempPath(), "habi-tests", "perf-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PerfStore(folder);
            Assert.Equal(0, store.PendingBytes);
            store.Append(Period(Operation("x", 1, 1, 1, 1, (0, 1))));
            store.Append(Period(Operation("y", 1, 1, 1, 1, (0, 1))));
            File.AppendAllText(Path.Combine(folder, $"perf-{Environment.ProcessId}.jsonl"), "{cut off" + Environment.NewLine);
            Assert.True(store.PendingBytes > 0);

            var (periods, files) = store.ReadPending();
            Assert.Equal(2, periods.Count);

            PerfStore.Delete(files);
            Assert.Equal(0, store.PendingBytes);
            Assert.Empty(store.ReadPending().Periods);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void Measuring_off_records_nothing()
    {
        Perf.Stop();
        using (Perf.Measure("not.measured"))
        {
        }

        Assert.DoesNotContain(Perf.TakePeriod()?.Operations ?? [], o => o.Name == "not.measured");
    }
}
