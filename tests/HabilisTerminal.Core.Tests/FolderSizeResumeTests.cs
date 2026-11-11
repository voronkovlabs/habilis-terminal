using System.Collections.Concurrent;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.Core.Tests;

/// <summary>Resumable folder counts (#37): a running count pauses for visible folders and continues later.</summary>
public class FolderSizeResumeTests
{
    [Fact]
    public void A_walk_paused_after_every_folder_ends_with_the_same_totals()
    {
        using var sandbox = new TestSandbox();
        var root = MakeTree(sandbox.Path, "tree", 30);

        var walk = new DirectorySize.Walk(root);
        var runs = 1;
        while (!walk.Run(pause: () => true))
        {
            runs++;
        }

        Assert.True(walk.IsDone);
        Assert.Equal(DirectorySize.Compute(root), walk.Totals);
        Assert.Equal(walk.Totals.Folders + 1, runs); // one folder per run
    }

    [Fact]
    public void A_cancelled_walk_keeps_its_place()
    {
        using var sandbox = new TestSandbox();
        var root = MakeTree(sandbox.Path, "tree", 10);
        var walk = new DirectorySize.Walk(root);
        Assert.False(walk.Run(pause: () => true));
        var before = walk.Totals;

        Assert.Throws<OperationCanceledException>(() => walk.Run(cancellationToken: new CancellationToken(canceled: true)));

        Assert.Equal(before, walk.Totals);
        Assert.True(walk.Run());
        Assert.Equal(DirectorySize.Compute(root), walk.Totals);
    }

    [Fact]
    public void Only_a_higher_ranked_visible_folder_interrupts_a_count()
    {
        var schedule = new FolderSizeSchedule();
        object left = new(), right = new();
        schedule.Set(left, ["big", "l2"]);
        schedule.SetVisible(left, ["big", "l2"]);
        Assert.Equal("big", schedule.TakeNext(out var owners));

        // The owner's own folders after the current one, and folders out of view, wait.
        Assert.False(schedule.ShouldYield("big", owners));
        schedule.Set(right, ["r1", "r2"]);
        Assert.False(schedule.ShouldYield("big", owners));

        // The other panel's visible folders, touched later, go first.
        schedule.SetVisible(right, ["r1"]);
        Assert.True(schedule.ShouldYield("big", owners));

        // The paused folder comes back after them, in its owner's display order.
        schedule.Restore("big", owners);
        Assert.Equal(["r1", "big", "l2", "r2"], Drain(schedule));
    }

    [Fact]
    public void A_folder_gone_before_its_count_is_one_that_could_not_be_read() =>
        Assert.Equal(new DirectorySize.Result(0, 0, 0, 1), DirectorySize.Compute(Path.Combine(Path.GetTempPath(), "habi-tests", "gone-" + Guid.NewGuid().ToString("N"))));

    [Fact]
    public void Worker_survives_a_folder_deleted_before_its_turn_and_counts_the_next()
    {
        // Issue #235: the folder sizes thread threw on a folder the panel had listed and then lost, and took the app down.
        using var sandbox = new TestSandbox();
        var gone = MakeTree(sandbox.Path, "gone", 1);
        var kept = MakeTree(sandbox.Path, "kept", 2);
        Directory.Delete(gone, recursive: true);

        var cache = new FolderSizeCache();
        using var worker = new FolderSizeWorker(cache, backgroundMode: false);
        worker.Request(new object(), [gone, kept]);
        WaitUntil(() => cache.HasFinal(kept));

        Assert.True(cache.TryGet(gone, out var size) && size.IsFinal);
        Assert.Equal(1, size.Totals.Inaccessible);
        Assert.Equal(DirectorySize.Compute(kept), cache.TryGet(kept, out var counted) ? counted.Totals : null);
    }

    [Fact]
    public void Worker_pauses_a_long_count_for_the_other_panels_visible_folder_and_resumes_it()
    {
        using var sandbox = new TestSandbox();
        var big = MakeTree(sandbox.Path, "big", 40);
        var small = MakeTree(sandbox.Path, "small", 2);
        var expected = DirectorySize.Compute(big);

        var cache = new FolderSizeCache();
        using var worker = new FolderSizeWorker(cache, backgroundMode: false);
        var reads = new ConcurrentQueue<string>();
        using var hold = new ManualResetEventSlim();
        worker.FolderRead = path =>
        {
            reads.Enqueue(path);
            if (path == big)
            {
                hold.Wait();
            }
        };

        object left = new(), right = new();
        worker.Request(left, [big]);
        worker.SetVisible(left, [big]);
        WaitUntil(() => !reads.IsEmpty);

        worker.Request(right, [small]);
        worker.SetVisible(right, [small]);
        hold.Set();
        WaitUntil(() => !worker.IsBusy);

        // big (one folder), then small, then the rest of big: no folder of big read twice.
        var order = reads.ToArray();
        Assert.Equal(big, order[0]);
        var firstSmall = Array.IndexOf(order, small);
        Assert.Equal(1, firstSmall);
        Assert.Contains(big, order[(firstSmall + 1)..]);
        Assert.Equal(expected.Folders, order.Count(p => p == big));
        Assert.True(cache.TryGet(big, out var size) && size.IsFinal);
        Assert.Equal(expected, size.Totals);
        Assert.True(cache.HasFinal(small));
        Assert.Equal(0, worker.PausedCount);
    }

    [Fact]
    public void A_count_paused_again_and_again_still_finishes_with_the_exact_total()
    {
        using var sandbox = new TestSandbox();
        var big = MakeTree(sandbox.Path, "big", 30);
        var smalls = Enumerable.Range(0, 10).Select(i => MakeTree(sandbox.Path, $"s{i}", 0)).ToArray();
        var expected = DirectorySize.Compute(big);

        var cache = new FolderSizeCache();
        using var worker = new FolderSizeWorker(cache, backgroundMode: false);
        var bigReads = 0;
        using var gate = new SemaphoreSlim(0);
        worker.FolderRead = path =>
        {
            if (path == big)
            {
                Interlocked.Increment(ref bigReads);
                gate.Wait(); // one folder of big per step of the test
            }
        };

        object left = new(), right = new();
        worker.Request(left, [big]);
        worker.SetVisible(left, [big]);
        foreach (var small in smalls)
        {
            WaitUntil(() => worker.Current == big && gate.CurrentCount == 0);
            worker.Request(right, [small]);
            worker.SetVisible(right, [small]);
            gate.Release();
            WaitUntil(() => cache.HasFinal(small));
            Assert.False(cache.HasFinal(big));
            Assert.True(cache.TryGet(big, out var running) && !running.IsFinal); // the paused total stays shown
        }

        gate.Release(1000);
        WaitUntil(() => !worker.IsBusy);

        Assert.True(cache.TryGet(big, out var size) && size.IsFinal);
        Assert.Equal(expected, size.Totals);
        Assert.Equal(expected.Folders, bigReads);
    }

    [Fact]
    public void Leaving_the_folder_drops_a_paused_count()
    {
        using var sandbox = new TestSandbox();
        var big = MakeTree(sandbox.Path, "big", 20);
        var small = MakeTree(sandbox.Path, "small", 2);

        var cache = new FolderSizeCache();
        using var worker = new FolderSizeWorker(cache, backgroundMode: false);
        using var holdBig = new ManualResetEventSlim();
        using var holdSmall = new ManualResetEventSlim();
        var reached = new ConcurrentDictionary<string, bool>();
        worker.FolderRead = path =>
        {
            reached[path] = true;
            (path == big ? holdBig : holdSmall).Wait();
        };

        object left = new(), right = new();
        worker.Request(left, [big]);
        worker.SetVisible(left, [big]);
        WaitUntil(() => reached.ContainsKey(big));
        worker.Request(right, [small]);
        worker.SetVisible(right, [small]);
        holdBig.Set();
        WaitUntil(() => reached.ContainsKey(small));
        Assert.Equal(1, worker.PausedCount);
        Assert.True(cache.TryGet(big, out var running) && !running.IsFinal);

        worker.Cancel(left);

        Assert.Equal(0, worker.PausedCount);
        Assert.False(cache.TryGet(big, out _));
        holdSmall.Set();
        WaitUntil(() => !worker.IsBusy);
        Assert.False(cache.TryGet(big, out _));
        Assert.True(cache.HasFinal(small));
    }

    /// <summary>A folder with <paramref name="children"/> subfolders, each with a subfolder and a file.</summary>
    private static string MakeTree(string parent, string name, int children)
    {
        var root = Path.Combine(parent, name);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "top.bin"), new byte[7]);
        for (var i = 0; i < children; i++)
        {
            var leaf = Path.Combine(root, $"d{i}", "x");
            Directory.CreateDirectory(leaf);
            File.WriteAllBytes(Path.Combine(leaf, "data.bin"), new byte[10 + i]);
        }

        return root;
    }

    private static string[] Drain(FolderSizeSchedule schedule)
    {
        var taken = new List<string>();
        while (schedule.TakeNext(out _) is { } next)
        {
            taken.Add(next);
        }

        return [.. taken];
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            Thread.Sleep(5);
        }
    }
}
