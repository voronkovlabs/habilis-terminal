using HabilisTerminal.Core.Operations;

namespace HabilisTerminal.Core.Tests;

/// <summary>F5 / F6 with several files copied at once (issue #392).</summary>
public class ParallelCopyTests
{
    private static readonly int[] LockedNumbers = [2, 9, 17, 25];

    private static OperationResult Copy(IReadOnlyList<string> sources, string destination, ScriptedHandler handler, OperationControl? control = null,
        bool move = false, ExistingFilePolicy existing = ExistingFilePolicy.Ask, int threads = 8)
    {
        using var own = new OperationControl();
        return new CopyOperation(sources, destination, new CopyOptions { Threads = threads, ExistingFiles = existing }, move, handler, control ?? own)
        {
            ForceCrossVolume = move,
            IgnoreSeekPenalty = true,
        }.Run();
    }

    /// <summary>A tree of <paramref name="folders"/> × <paramref name="files"/> files with distinct contents and old folder times.</summary>
    private static string Tree(string root, int folders, int files)
    {
        for (var d = 0; d < folders; d++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, $"d{d}", "inner")).FullName;
            for (var f = 0; f < files; f++)
            {
                File.WriteAllText(Path.Combine(folder, $"f{f}.txt"), $"{d}:{f}:" + new string('x', f * 37));
            }

            Directory.SetLastWriteTimeUtc(folder, new DateTime(2019, 1, 1 + d, 0, 0, 0, DateTimeKind.Utc));
            Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(folder)!, new DateTime(2018, 2, 1 + d, 0, 0, 0, DateTimeKind.Utc));
        }

        Directory.SetLastWriteTimeUtc(root, new DateTime(2017, 3, 4, 0, 0, 0, DateTimeKind.Utc));
        return root;
    }

    private static string[] Relative(string root) =>
        [.. Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p)).Order(StringComparer.Ordinal)];

    [Fact]
    public void A_tree_is_copied_whole_with_folder_times()
    {
        using var sandbox = new TestSandbox();
        var source = Tree(Path.Combine(sandbox.Path, "src"), folders: 6, files: 40);
        var destination = Path.Combine(sandbox.Path, "dst");
        var handler = new ScriptedHandler();

        var result = Copy([source], destination, handler);

        Assert.Equal(240, result.Succeeded);
        Assert.Equal(Relative(source), Relative(destination));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            Assert.Equal(File.ReadAllText(file), File.ReadAllText(Path.Combine(destination, Path.GetRelativePath(source, file))));
        }

        foreach (var folder in Directory.GetDirectories(source, "*", SearchOption.AllDirectories).Append(source))
        {
            Assert.Equal(Directory.GetLastWriteTimeUtc(folder), Directory.GetLastWriteTimeUtc(Path.Combine(destination, Path.GetRelativePath(source, folder))));
        }

        Assert.Equal(destination, result.TargetPaths[^1]);
        Assert.Equal(handler.LastProgress!.TotalBytes, handler.LastProgress.DoneBytes);
        Assert.Equal(handler.LastProgress.TotalItems, handler.LastProgress.DoneItems);
    }

    [Fact]
    public void Files_are_copied_in_parallel()
    {
        using var sandbox = new TestSandbox();
        var source = Tree(Path.Combine(sandbox.Path, "src"), folders: 2, files: 50);
        var threads = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();
        var handler = new ScriptedHandler { OnProgress = _ => threads[Environment.CurrentManagedThreadId] = true };

        Copy([source], Path.Combine(sandbox.Path, "dst"), handler);

        Assert.True(threads.Count > 1, "progress came from one thread only");
    }

    [Fact]
    public void One_thread_copies_on_the_operation_thread()
    {
        using var sandbox = new TestSandbox();
        var source = Tree(Path.Combine(sandbox.Path, "src"), folders: 1, files: 5);
        var threads = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();
        var handler = new ScriptedHandler { OnProgress = _ => threads[Environment.CurrentManagedThreadId] = true };

        Copy([source], Path.Combine(sandbox.Path, "dst"), handler, threads: 1);

        Assert.Equal(Environment.CurrentManagedThreadId, Assert.Single(threads.Keys));
    }

    [Fact]
    public void Merging_into_an_existing_tree_asks_once_per_existing_file()
    {
        using var sandbox = new TestSandbox();
        var source = Tree(Path.Combine(sandbox.Path, "src"), folders: 2, files: 10);
        // Copying src into dst puts it at dst\src, which exists in part.
        var destination = Path.Combine(sandbox.Path, "dst", "src");
        Directory.CreateDirectory(Path.Combine(destination, "d0", "inner"));
        File.WriteAllText(Path.Combine(destination, "d0", "inner", "f3.txt"), "old");
        File.WriteAllText(Path.Combine(destination, "d0", "inner", "f7.txt"), "old");
        var handler = new ScriptedHandler { OnConflict = _ => new ConflictDecision(ConflictAction.Skip) };

        var result = Copy([source], Path.Combine(sandbox.Path, "dst"), handler);

        Assert.Equal(2, handler.Conflicts.Count);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(18, result.Succeeded);
        Assert.Equal("old", File.ReadAllText(Path.Combine(destination, "d0", "inner", "f3.txt")));
        Assert.Equal(File.ReadAllText(Path.Combine(source, "d1", "inner", "f3.txt")), File.ReadAllText(Path.Combine(destination, "d1", "inner", "f3.txt")));
    }

    [WindowsFact]
    public void Skip_all_asks_once_while_other_files_go_on()
    {
        using var sandbox = new TestSandbox();
        var source = Tree(Path.Combine(sandbox.Path, "src"), folders: 1, files: 30);
        var folder = Path.Combine(source, "d0", "inner");
        var locks = LockedNumbers.Select(i => new FileStream(Path.Combine(folder, $"f{i}.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)).ToArray();
        var handler = new ScriptedHandler { OnError = _ => ErrorAction.SkipAll };
        OperationResult result;
        try
        {
            result = Copy([source], Path.Combine(sandbox.Path, "dst"), handler);
        }
        finally
        {
            foreach (var stream in locks)
            {
                stream.Dispose();
            }
        }

        Assert.Single(handler.Errors);
        Assert.Equal(4, result.Failed);
        Assert.Equal(26, result.Succeeded);
    }

    [WindowsFact]
    public void Move_to_another_volume_deletes_sources_and_emptied_folders()
    {
        using var sandbox = new TestSandbox();
        var source = Tree(Path.Combine(sandbox.Path, "src"), folders: 3, files: 20);
        var locked = Path.Combine(source, "d1", "inner", "f4.txt");
        var handler = new ScriptedHandler { OnError = _ => ErrorAction.Skip };
        var destination = Path.Combine(sandbox.Path, "dst");

        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Copy([source], destination, handler, move: true);
        }

        Assert.False(Directory.Exists(Path.Combine(source, "d0")));
        Assert.False(Directory.Exists(Path.Combine(source, "d2")));
        Assert.Equal(["d1", Path.Combine("d1", "inner"), Path.Combine("d1", "inner", "f4.txt")], Relative(source));
        Assert.Equal(59, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Cancelling_stops_every_thread_before_run_returns()
    {
        using var sandbox = new TestSandbox();
        var source = Tree(Path.Combine(sandbox.Path, "src"), folders: 4, files: 100);
        var destination = Path.Combine(sandbox.Path, "dst");
        using var control = new OperationControl();
        var handler = new ScriptedHandler { OnProgress = p => { if (p.DoneItems > 20) { control.Cancel(); } } };

        Assert.ThrowsAny<OperationCanceledException>(() => Copy([source], destination, handler, control));

        var count = Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length;
        Thread.Sleep(300);
        Assert.Equal(count, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
        Assert.True(count < 400);
    }

    [Fact]
    public void Several_sources_keep_the_last_one_last()
    {
        using var sandbox = new TestSandbox();
        var sources = Enumerable.Range(0, 30).Select(i =>
        {
            var path = Path.Combine(sandbox.Path, "src", $"n{i:D2}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new string('z', (30 - i) * 1000));
            return path;
        }).ToArray();

        var result = Copy(sources, Path.Combine(sandbox.Path, "dst") + Path.DirectorySeparatorChar, new ScriptedHandler());

        Assert.Equal(sources.Select(s => Path.Combine(sandbox.Path, "dst", Path.GetFileName(s))), result.TargetPaths);
    }
}
