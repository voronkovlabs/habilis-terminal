using System.Security.AccessControl;
using System.Security.Principal;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Operations;

namespace HabilisTerminal.Core.Tests;

public class DeleteAndOtherOperationTests
{
    private static OperationResult Delete(IReadOnlyList<string> paths, bool permanent, ScriptedHandler handler)
    {
        using var control = new OperationControl();
        return new DeleteOperation(paths, permanent, handler, control).Run();
    }

    [Fact]
    public void Permanent_delete_removes_trees_and_read_only_files_after_confirmation()
    {
        using var sandbox = new TestSandbox();
        var tree = Directory.CreateDirectory(Path.Combine(sandbox.Path, "tree", "deep")).FullName;
        var readOnly = Path.Combine(tree, "ro.txt");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var handler = new ScriptedHandler { OnConfirm = _ => ConfirmationAnswer.YesToAll };

        Delete([Path.Combine(sandbox.Path, "tree")], permanent: true, handler);

        Assert.False(Directory.Exists(Path.Combine(sandbox.Path, "tree")));
        Assert.Contains(handler.Confirmations, c => c.Kind == ConfirmationKind.DeleteNonEmptyDirectory);
        Assert.Contains(handler.Confirmations, c => c.Kind == ConfirmationKind.DeleteProtected);
    }

    [Fact]
    public void Declining_a_non_empty_directory_keeps_it()
    {
        using var sandbox = new TestSandbox();
        var tree = Directory.CreateDirectory(Path.Combine(sandbox.Path, "tree")).FullName;
        File.WriteAllText(Path.Combine(tree, "a.txt"), "x");

        var result = Delete([tree], permanent: true, new ScriptedHandler { OnConfirm = _ => ConfirmationAnswer.No });

        Assert.True(File.Exists(Path.Combine(tree, "a.txt")));
        Assert.Equal(1, result.Skipped);
    }

    [WindowsFact]
    public void Deleting_a_junction_never_touches_its_target()
    {
        using var sandbox = new TestSandbox();
        var realTarget = Directory.CreateDirectory(Path.Combine(sandbox.Path, "real")).FullName;
        File.WriteAllText(Path.Combine(realTarget, "precious.txt"), "keep me");
        var tree = Directory.CreateDirectory(Path.Combine(sandbox.Path, "tree")).FullName;
        CopyMoveTests.RunCmd($"mklink /J \"{Path.Combine(tree, "link")}\" \"{realTarget}\"");
        var directLink = Path.Combine(sandbox.Path, "direct-link");
        CopyMoveTests.RunCmd($"mklink /J \"{directLink}\" \"{realTarget}\"");

        Delete([tree, directLink], permanent: true, new ScriptedHandler { OnConfirm = _ => ConfirmationAnswer.YesToAll });

        Assert.False(Directory.Exists(tree));
        Assert.False(Directory.Exists(directLink));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(realTarget, "precious.txt")));
    }

    [WindowsFact]
    public void Locked_files_report_the_process_and_can_be_skipped()
    {
        using var sandbox = new TestSandbox();
        var file = Path.Combine(sandbox.Path, "locked.txt");
        File.WriteAllText(file, "x");
        var handler = new ScriptedHandler { OnError = _ => ErrorAction.Skip };

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Delete([file], permanent: true, handler);
        }

        Assert.True(File.Exists(file));
        Assert.NotNull(Assert.Single(handler.Errors).LockingProcesses);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [WindowsFact]
    public void Access_denied_is_reported_and_skip_all_stops_asking()
    {
        using var sandbox = new TestSandbox();
        var locked = Directory.CreateDirectory(Path.Combine(sandbox.Path, "locked")).FullName;
        var files = Enumerable.Range(0, 3).Select(i => Path.Combine(locked, $"f{i}.txt")).ToArray();
        foreach (var file in files)
        {
            File.WriteAllText(file, "x");
        }

        var user = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(user, FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles, InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);
        var info = new DirectoryInfo(locked);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            var handler = new ScriptedHandler { OnError = _ => ErrorAction.SkipAll };

            var result = Delete(files, permanent: true, handler);

            Assert.Single(handler.Errors);
            Assert.Equal(3, result.Failed);
            Assert.All(files, f => Assert.True(File.Exists(f)));
        }
        finally
        {
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
    }

    [Fact]
    public void Recycle_bin_delete_on_a_fixed_drive()
    {
        using var sandbox = new TestSandbox();
        var file = Path.Combine(sandbox.Path, "habi-test-recycle.txt");
        File.WriteAllText(file, "test file from Habilis Terminal tests");
        if (Interop.RecycleBin.CannotRecycleReason(file) is { } reason)
        {
            Assert.Skip($"Recycle Bin not available here: {reason}");
        }

        var result = Delete([file], permanent: false, new ScriptedHandler());

        Assert.False(File.Exists(file));
        Assert.Equal(1, result.Succeeded);
    }

    [WindowsFact]
    public void Paths_too_long_for_the_recycle_bin_ask_before_permanent_delete()
    {
        using var sandbox = new TestSandbox();
        var deep = sandbox.Path;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(deep);
        var file = Path.Combine(deep, "long.txt");
        File.WriteAllText(file, "x");
        var handler = new ScriptedHandler { OnConfirm = c => c.Kind == ConfirmationKind.DeletePermanently ? ConfirmationAnswer.No : ConfirmationAnswer.Cancel };

        Delete([file], permanent: false, handler);

        Assert.True(File.Exists(file), "nothing is deleted permanently without a yes");
        Assert.Equal(ConfirmationKind.DeletePermanently, Assert.Single(handler.Confirmations).Kind);
    }

    [Fact]
    public void Attributes_and_times_are_set_recursively_without_entering_links()
    {
        using var sandbox = new TestSandbox();
        var tree = Directory.CreateDirectory(Path.Combine(sandbox.Path, "tree")).FullName;
        var file = Path.Combine(tree, "a.txt");
        File.WriteAllText(file, "x");
        var time = new DateTime(2021, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var change = new AttributeChange { Set = FileAttributes.ReadOnly | FileAttributes.Hidden, LastWriteTimeUtc = time, Recursive = true };

        using var control = new OperationControl();
        new AttributesOperation([tree], change, new ScriptedHandler(), control).Run();

        Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
        Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.Hidden));
        Assert.Equal(time, File.GetLastWriteTimeUtc(file));

        using var control2 = new OperationControl();
        new AttributesOperation([file], new AttributeChange { Clear = FileAttributes.ReadOnly | FileAttributes.Hidden }, new ScriptedHandler(), control2).Run();
        Assert.False(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void Ntfs_compression_can_be_switched()
    {
        using var sandbox = new TestSandbox();
        var file = Path.Combine(sandbox.Path, "c.txt");
        File.WriteAllText(file, new string('c', 10000));
        if (!new DriveInfo(Path.GetPathRoot(file)!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("Not an NTFS volume.");
        }

        using var control = new OperationControl();
        new AttributesOperation([file], new AttributeChange { Compressed = true }, new ScriptedHandler(), control).Run();

        Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.Compressed));
    }

    [WindowsFact]
    public void Directory_creator_handles_nesting_lists_and_errors()
    {
        using var sandbox = new TestSandbox();
        File.WriteAllText(Path.Combine(sandbox.Path, "file"), "x");

        var created = DirectoryCreator.Create(sandbox.Path, @"a\b\c; second ;a", multipleNames: true);

        Assert.Equal(3, created.Count);
        Assert.True(Directory.Exists(Path.Combine(sandbox.Path, "a", "b", "c")));
        Assert.True(Directory.Exists(Path.Combine(sandbox.Path, "second")));
        Assert.Throws<IOException>(() => DirectoryCreator.Create(sandbox.Path, "file", multipleNames: false));
        Assert.Throws<ArgumentException>(() => DirectoryCreator.Create(sandbox.Path, "bad.", multipleNames: false));
        Assert.Throws<ArgumentException>(() => DirectoryCreator.Create(sandbox.Path, @"x\CON", multipleNames: false));
    }

    [UnixFact]
    public void Directory_creator_on_a_mac_handles_nesting_lists_and_errors()
    {
        using var sandbox = new TestSandbox();
        File.WriteAllText(Path.Combine(sandbox.Path, "file"), "x");

        var created = DirectoryCreator.Create(sandbox.Path, "a/b/c; second ;a", multipleNames: true);

        Assert.Equal(3, created.Count);
        Assert.True(Directory.Exists(Path.Combine(sandbox.Path, "a", "b", "c")));
        Assert.True(Directory.Exists(Path.Combine(sandbox.Path, "second")));
        Assert.Throws<IOException>(() => DirectoryCreator.Create(sandbox.Path, "file", multipleNames: false));
        Assert.Throws<ArgumentException>(() => DirectoryCreator.Create(sandbox.Path, "x/..", multipleNames: false));
        Assert.Equal(Path.Combine(sandbox.Path, "dot."), DirectoryCreator.Create(sandbox.Path, "dot.", multipleNames: false)[0]);
    }

    [UnixTheory]
    [InlineData("report.txt", null)]
    [InlineData("CON", null)]
    [InlineData("name.", null)]
    [InlineData("a/b", "characters")]
    [InlineData("a:b", "characters")]
    [InlineData("..", "not a valid")]
    [InlineData("", "empty")]
    public void Name_rules_on_a_mac(string name, string? errorContains)
    {
        if (name.Contains(':') && !OperatingSystem.IsMacOS())
        {
            return; // ':' is the Finder's own rule
        }

        var error = NameRules.Validate(name);
        if (errorContains is null)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.Contains(errorContains, error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [WindowsTheory]
    [InlineData("report.txt", null)]
    [InlineData("CON", "reserved")]
    [InlineData("com1.log", "reserved")]
    [InlineData("name.", "dot")]
    [InlineData("name ", "dot")]
    [InlineData("a<b", "characters")]
    [InlineData("", "empty")]
    public void Name_rules(string name, string? errorContains)
    {
        var error = NameRules.Validate(name);

        if (errorContains is null)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.Contains(errorContains, error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("file.txt", "*.bak", "file.bak")]
    [InlineData("file.txt", "new_*", "new_file.txt")]
    [InlineData("file.txt", "*.*", "file.txt")]
    [InlineData("photo.jpeg", "???.*", "pho.jpeg")]
    [InlineData("archive.tar.gz", "*.zip", "archive.tar.zip")]
    [InlineData("noext", "*.txt", "noext.txt")]
    public void Rename_masks_follow_far(string name, string mask, string expected)
    {
        Assert.Equal(expected, NameRules.ApplyRenameMask(name, mask));
    }

    [Fact]
    public void Next_free_name_counts_up()
    {
        var taken = new HashSet<string> { "a (2).txt", "a (3).txt" };

        Assert.Equal("a (4).txt", NameRules.NextFreeName("a.txt", taken.Contains));
    }

    [Fact]
    public void Folder_compare_selects_missing_and_newer_files()
    {
        var day = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        FileEntry F(string name, int seconds, long size = 1) => new(name, FileAttributes.Normal, size, day, day.AddSeconds(seconds), day);
        FileEntry D(string name) => new(name, FileAttributes.Directory, 0, day, day, day);
        FileEntry[] left = [F("only-left", 0), F("newer-left", 10), F("same", 0), F("fat", 1), F("size", 0, 1), D("dir-both"), D("dir-left")];
        FileEntry[] right = [F("only-right", 0), F("newer-left", 0), F("same", 0), F("fat", 0), F("size", 0, 2), D("dir-both")];

        var (selectLeft, selectRight) = FolderComparer.Compare(left, right, compareSize: true, fatTimePrecision: true);

        Assert.Equal(["only-left", "newer-left", "size", "dir-left"], selectLeft);
        Assert.Equal(["size", "only-right"], selectRight);
    }

    [Fact]
    public async Task Scheduler_queues_operations_on_the_same_volume()
    {
        var scheduler = new OperationScheduler();
        var running = 0;
        var maximum = 0;
        var path = Path.GetTempPath();

        int Work()
        {
            var now = Interlocked.Increment(ref running);
            maximum = Math.Max(maximum, now);
            Thread.Sleep(100);
            Interlocked.Decrement(ref running);
            return now;
        }

        await Task.WhenAll(scheduler.RunAsync([path], Work, TestContext.Current.CancellationToken), scheduler.RunAsync([path], Work, TestContext.Current.CancellationToken));

        Assert.Equal(1, maximum);
    }
}
