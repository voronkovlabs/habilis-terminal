using System.Globalization;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.Core.Tests;

public class FolderSizeTests
{
    private static readonly DirectorySize.Result Some = new(100, 1, 0, 0);

    private static string[] Drain(FolderSizeSchedule schedule)
    {
        var taken = new List<string>();
        while (schedule.TakeNext(out _) is { } next)
        {
            taken.Add(next);
        }

        return [.. taken];
    }

    [Fact]
    public void Visible_folders_go_first_then_the_rest_in_listing_order()
    {
        var schedule = new FolderSizeSchedule();
        var left = new object();
        schedule.Set(left, ["a", "b", "c", "d", "e"]);
        schedule.SetVisible(left, ["d", "c"]);

        Assert.Equal(["d", "c", "a", "b", "e"], Drain(schedule));
    }

    [Fact]
    public void Scrolling_reorders_without_dropping_anything()
    {
        var schedule = new FolderSizeSchedule();
        var left = new object();
        schedule.Set(left, ["a", "b", "c", "d"]);
        schedule.SetVisible(left, ["a", "b"]);
        Assert.Equal("a", schedule.TakeNext(out _));

        schedule.SetVisible(left, ["c", "d"]);

        Assert.Equal(["c", "d", "b"], Drain(schedule));
    }

    [Fact]
    public void The_panel_that_scrolled_last_goes_first_and_shared_folders_are_counted_once()
    {
        var schedule = new FolderSizeSchedule();
        object left = new(), right = new();
        schedule.Set(left, ["x", "shared"]);
        schedule.Set(right, ["shared", "y"]);
        schedule.SetVisible(left, ["x"]);
        schedule.SetVisible(right, ["shared"]);

        Assert.Equal("shared", schedule.TakeNext(out var owners));
        Assert.Equal([left, right], owners.OrderBy(o => o == left ? 0 : 1));
        Assert.Equal(["x", "y"], Drain(schedule));
    }

    [Fact]
    public void Cancel_drops_only_that_owners_requests()
    {
        var schedule = new FolderSizeSchedule();
        object left = new(), right = new();
        schedule.Set(left, ["a", "both"]);
        schedule.Set(right, ["both", "b"]);

        schedule.Cancel(left);

        Assert.False(schedule.IsWanted("a"));
        Assert.Equal(["both", "b"], Drain(schedule));
    }

    [Fact]
    public void Set_replaces_the_previous_request()
    {
        var schedule = new FolderSizeSchedule();
        var left = new object();
        schedule.Set(left, ["old1", "old2"]);
        schedule.Set(left, ["new"]);

        Assert.Equal(["new"], Drain(schedule));
        Assert.True(schedule.IsEmpty);
    }

    [Fact]
    public void Invalidate_drops_the_path_its_ancestors_and_descendants_only()
    {
        var cache = new FolderSizeCache();
        foreach (var path in new[] { P(@"C:\a"), P(@"C:\a\b"), P(@"C:\a\b\c"), P(@"C:\a\bb"), P(@"C:\x") })
        {
            cache.Set(path, Some);
        }

        var announced = new List<string>();
        cache.Invalidated += announced.Add;

        cache.Invalidate(P(@"C:\a\b\"));

        Assert.Equal([P(@"C:\a\b")], announced);
        Assert.False(cache.TryGet(P(@"C:\a"), out _));
        Assert.False(cache.TryGet(P(@"C:\a\b"), out _));
        Assert.False(cache.TryGet(P(@"C:\a\b\c"), out _));
        Assert.True(cache.TryGet(P(@"C:\a\bb"), out _));
        Assert.True(cache.TryGet(P(@"C:\X"), out _));
    }

    [Fact]
    public void A_result_started_before_a_related_invalidation_is_dropped()
    {
        var cache = new FolderSizeCache();
        var stamp = cache.Stamp;
        cache.Invalidate(P(@"C:\a\b\file.txt"));

        Assert.False(cache.Set(P(@"C:\a"), Some, startStamp: stamp));
        Assert.True(cache.Set(P(@"C:\other"), Some, startStamp: stamp));
        Assert.True(cache.Set(P(@"C:\a"), Some, startStamp: cache.Stamp));
    }

    [Fact]
    public void A_running_total_never_replaces_a_final_value_and_can_be_dropped()
    {
        var cache = new FolderSizeCache();
        cache.Set(@"C:\a", Some);
        Assert.False(cache.Set(@"C:\a", new DirectorySize.Result(5, 1, 0, 0), isFinal: false));

        cache.Set(@"C:\b", Some, isFinal: false);
        cache.RemoveRunning(@"C:\a");
        cache.RemoveRunning(@"C:\b");

        Assert.True(cache.HasFinal(@"C:\a"));
        Assert.False(cache.TryGet(@"C:\b", out _));
    }

    [Fact]
    public void Sizes_are_formatted_exact_or_compact_with_a_lower_bound_marker()
    {
        var culture = CultureInfo.InvariantCulture;
        var complete = new FolderSize(new DirectorySize.Result(1_288_490_189, 10, 2, 0), IsFinal: true);
        var partial = new FolderSize(new DirectorySize.Result(1_288_490_189, 10, 2, 1), IsFinal: true);

        Assert.Equal("1,288,490,189", SizeFormat.Folder(complete, compact: false, culture));
        Assert.Equal("1.2 GB", SizeFormat.Folder(complete, compact: true, culture));
        Assert.Equal("≥ 1.2 GB", SizeFormat.Folder(partial, compact: true, culture));
        Assert.Equal("512 B", SizeFormat.Compact(512, culture));
        Assert.Equal("34 MB", SizeFormat.Compact(34L * 1024 * 1024, culture));
    }

    [Fact]
    public void Sorting_by_size_uses_known_folder_sizes()
    {
        var day = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        FileEntry Dir(string name) => new(name, FileAttributes.Directory, 0, day, day, day);
        FileEntry[] entries = [Dir("small"), Dir("big"), Dir("unknown")];
        var sizes = new Dictionary<string, long> { ["small"] = 10, ["big"] = 1000 };

        var sorted = EntrySorter.Sort(entries, new SortOptions(SortMode.Size, Descending: true), CultureInfo.InvariantCulture,
            e => sizes.TryGetValue(e.Name, out var size) ? size : null);

        Assert.Equal(["big", "small", "unknown"], sorted.Select(e => e.Name));
    }

    [Fact]
    public void Worker_sizes_requested_folders_and_cancel_stops_the_rest()
    {
        using var sandbox = new TestSandbox();
        var folders = Enumerable.Range(0, 3).Select(i => Path.Combine(sandbox.Path, $"f{i}")).ToArray();
        foreach (var (folder, i) in folders.Select((f, i) => (f, i)))
        {
            Directory.CreateDirectory(Path.Combine(folder, "sub"));
            File.WriteAllBytes(Path.Combine(folder, "sub", "data.bin"), new byte[100 * (i + 1)]);
        }

        var cache = new FolderSizeCache();
        using var worker = new FolderSizeWorker(cache, backgroundMode: false);
        var owner = new object();
        worker.Request(owner, folders);
        WaitUntil(() => !worker.IsBusy);

        Assert.Equal([100L, 200L, 300L], folders.Select(f => cache.TryGet(f, out var size) && size.IsFinal ? size.Bytes : -1));

        // Requests skip folders already sized; cancelling forgets what is pending.
        cache.Invalidate(folders[0]);
        worker.Request(owner, folders);
        worker.Cancel(owner);
        WaitUntil(() => !worker.IsBusy);
        Assert.True(cache.HasFinal(folders[2]));
        Assert.False(cache.TryGet(folders[0], out var stale) && !stale.IsFinal);
    }

    [Fact]
    public void Worker_cancel_stops_a_running_count()
    {
        using var sandbox = new TestSandbox();
        var big = Path.Combine(sandbox.Path, "big");
        for (var i = 0; i < 400; i++)
        {
            Directory.CreateDirectory(Path.Combine(big, $"d{i}", "x"));
        }

        var cache = new FolderSizeCache();
        using var worker = new FolderSizeWorker(cache, backgroundMode: false);
        var owner = new object();
        worker.Request(owner, [big]);
        WaitUntil(() => worker.Current is not null || cache.HasFinal(big));
        worker.Cancel(owner);
        WaitUntil(() => !worker.IsBusy);

        // Either the count finished before the cancel arrived or it left nothing behind.
        Assert.True(cache.HasFinal(big) || !cache.TryGet(big, out _));
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            Thread.Sleep(10);
        }
    }

    /// <summary>A Windows path of the cases as this system writes it: <c>C:\a\b</c> is <c>/a/b</c> off Windows (#259).</summary>
    private static string P(string windowsPath) =>
        OperatingSystem.IsWindows() ? windowsPath : windowsPath[2..].Replace('\\', '/');
}
