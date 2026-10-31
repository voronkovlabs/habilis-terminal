using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Safety;

namespace HabilisTerminal.Core.Tests;

public class WatcherAndUpdaterTests
{
    [Fact]
    public async Task Watcher_reports_a_burst_of_changes_as_one_batch()
    {
        using var sandbox = new TestSandbox();
        using var watcher = new DirectoryWatcher(sandbox.Path, TimeSpan.FromMilliseconds(300));
        var batches = new List<DirectoryChanges>();
        watcher.Changed += changes => { lock (batches) { batches.Add(changes); } };
        watcher.Start();

        for (var i = 0; i < 20; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(sandbox.Path, $"f{i}.txt"), "x", TestContext.Current.CancellationToken);
        }

        await WaitUntilAsync(() => { lock (batches) { return batches.Sum(b => b.Names.Count) >= 20; } });
        lock (batches)
        {
            Assert.True(batches.Count <= 2, $"Expected the burst in at most 2 batches, got {batches.Count}.");
            Assert.All(batches, b => Assert.False(b.RequiresFullRefresh));
        }
    }

    [Fact]
    public async Task Watcher_errors_request_a_full_refresh()
    {
        using var sandbox = new TestSandbox();
        using var watcher = new DirectoryWatcher(sandbox.Path, TimeSpan.FromMilliseconds(50));
        DirectoryChanges? received = null;
        watcher.Changed += changes => received = changes;
        watcher.Start();

        watcher.RequestFullRefresh();

        await WaitUntilAsync(() => received is not null);
        Assert.True(received!.RequiresFullRefresh);
    }

    [Fact]
    public void Updater_applies_created_changed_and_deleted_entries()
    {
        using var sandbox = new TestSandbox();
        File.WriteAllText(Path.Combine(sandbox.Path, "keep.txt"), "1");
        File.WriteAllText(Path.Combine(sandbox.Path, "grow.txt"), "1");
        File.WriteAllText(Path.Combine(sandbox.Path, "gone.txt"), "1");
        var listing = DirectoryReader.ReadAll(sandbox.Path);

        File.WriteAllText(Path.Combine(sandbox.Path, "grow.txt"), "12345");
        File.Delete(Path.Combine(sandbox.Path, "gone.txt"));
        File.WriteAllText(Path.Combine(sandbox.Path, "new.txt"), "1");
        var changes = new DirectoryChanges(["grow.txt", "gone.txt", "new.txt"], RequiresFullRefresh: false);

        var updated = ListingUpdater.Apply(sandbox.Path, listing, changes)!.ToDictionary(e => e.Name);

        Assert.Equal(["grow.txt", "keep.txt", "new.txt"], updated.Keys.Order());
        Assert.Equal(5, updated["grow.txt"].Length);
    }

    [Fact]
    public void Updater_asks_for_a_full_read_on_overflow_or_large_bursts()
    {
        var many = Enumerable.Range(0, ListingUpdater.MaxIncrementalChanges + 1).Select(i => $"f{i}").ToArray();

        Assert.Null(ListingUpdater.Apply("C:\\", [], new DirectoryChanges([], RequiresFullRefresh: true)));
        Assert.Null(ListingUpdater.Apply("C:\\", [], new DirectoryChanges(many, RequiresFullRefresh: false)));
    }

    [Fact]
    public void Guard_blocks_paths_outside_the_allowed_area()
    {
        using var sandbox = new TestSandbox();
        using var scope = DestructiveOperationGuard.Restrict(sandbox.Contains);

        DestructiveOperationGuard.Check(Path.Combine(sandbox.Path, "inside.txt"));
        Assert.Throws<InvalidOperationException>(() => DestructiveOperationGuard.Check(@"C:\Windows\notepad.exe"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(50);
        }
    }
}
