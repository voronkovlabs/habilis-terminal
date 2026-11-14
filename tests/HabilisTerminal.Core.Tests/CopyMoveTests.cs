using System.Diagnostics;
using HabilisTerminal.Core.Operations;

namespace HabilisTerminal.Core.Tests;

public class CopyMoveTests
{
    private static OperationResult Copy(IReadOnlyList<string> sources, string destination, ScriptedHandler handler, CopyOptions? options = null, bool move = false, bool forceCrossVolume = false)
    {
        using var control = new OperationControl();
        return new CopyOperation(sources, destination, options ?? new CopyOptions(), move, handler, control) { ForceCrossVolume = forceCrossVolume }.Run();
    }

    private static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [WindowsFact]
    public void Copies_files_and_trees_into_an_existing_directory_with_times_and_attributes()
    {
        using var sandbox = new TestSandbox();
        var file = Write(Path.Combine(sandbox.Path, "src", "a.txt"), "hello");
        Write(Path.Combine(sandbox.Path, "src", "tree", "deep", "b.txt"), "bb");
        File.SetAttributes(file, FileAttributes.Archive | FileAttributes.Hidden);
        var modified = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, modified);
        var target = Directory.CreateDirectory(Path.Combine(sandbox.Path, "dst")).FullName;

        var result = Copy([file, Path.Combine(sandbox.Path, "src", "tree")], target, new ScriptedHandler());

        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.Equal("bb", File.ReadAllText(Path.Combine(target, "tree", "deep", "b.txt")));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(target, "a.txt")));
        Assert.True(File.GetAttributes(Path.Combine(target, "a.txt")).HasFlag(FileAttributes.Hidden));
        Assert.True(File.Exists(file));
        Assert.Equal(2, result.Succeeded);
    }

    [Theory]
    [InlineData(@"copy.txt", "copy.txt")]
    [InlineData(@"*.bak", "a.bak")]
    [InlineData(@"new_*", "new_a.txt")]
    public void Single_source_gets_a_new_name_or_a_mask(string destinationName, string expected)
    {
        using var sandbox = new TestSandbox();
        var file = Write(Path.Combine(sandbox.Path, "a.txt"), "x");

        Copy([file], Path.Combine(sandbox.Path, destinationName), new ScriptedHandler());

        Assert.True(File.Exists(Path.Combine(sandbox.Path, expected)));
    }

    [Fact]
    public void Several_sources_create_the_destination_directory()
    {
        using var sandbox = new TestSandbox();
        var a = Write(Path.Combine(sandbox.Path, "a.txt"), "a");
        var b = Write(Path.Combine(sandbox.Path, "b.txt"), "b");

        Copy([a, b], Path.Combine(sandbox.Path, "new folder"), new ScriptedHandler());

        Assert.True(File.Exists(Path.Combine(sandbox.Path, "new folder", "a.txt")));
        Assert.True(File.Exists(Path.Combine(sandbox.Path, "new folder", "b.txt")));
    }

    [Fact]
    public void Copying_a_directory_into_itself_is_refused()
    {
        using var sandbox = new TestSandbox();
        var directory = Directory.CreateDirectory(Path.Combine(sandbox.Path, "dir")).FullName;

        Assert.Throws<OperationRefusedException>(() => Copy([directory], Path.Combine(directory, "inner") + Path.DirectorySeparatorChar, new ScriptedHandler()));
    }

    [Fact]
    public void Reserved_target_names_are_refused()
    {
        using var sandbox = new TestSandbox();
        var file = Write(Path.Combine(sandbox.Path, "a.txt"), "x");

        Assert.Throws<OperationRefusedException>(() => Copy([file], Path.Combine(sandbox.Path, "NUL.txt"), new ScriptedHandler()));
    }

    [Theory]
    [InlineData(ConflictAction.Overwrite, "new", null)]
    [InlineData(ConflictAction.Skip, "old", null)]
    [InlineData(ConflictAction.Append, "oldnew", null)]
    [InlineData(ConflictAction.Rename, "old", "a (2).txt")]
    public void Conflict_choices(ConflictAction action, string expectedTarget, string? renamed)
    {
        using var sandbox = new TestSandbox();
        var source = Write(Path.Combine(sandbox.Path, "src", "a.txt"), "new");
        var target = Write(Path.Combine(sandbox.Path, "dst", "a.txt"), "old");
        var handler = new ScriptedHandler { OnConflict = _ => new ConflictDecision(action) };

        Copy([source], Path.Combine(sandbox.Path, "dst") + Path.DirectorySeparatorChar, handler);

        Assert.Single(handler.Conflicts);
        Assert.Equal(expectedTarget, File.ReadAllText(target));
        if (renamed is not null)
        {
            Assert.Equal("new", File.ReadAllText(Path.Combine(sandbox.Path, "dst", renamed)));
        }
    }

    [Fact]
    public void Overwrite_if_newer_and_apply_to_all()
    {
        using var sandbox = new TestSandbox();
        var newer = Write(Path.Combine(sandbox.Path, "src", "newer.txt"), "src");
        var older = Write(Path.Combine(sandbox.Path, "src", "older.txt"), "src");
        Write(Path.Combine(sandbox.Path, "dst", "newer.txt"), "dst");
        Write(Path.Combine(sandbox.Path, "dst", "older.txt"), "dst");
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddHours(1));
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));
        var handler = new ScriptedHandler { OnConflict = _ => new ConflictDecision(ConflictAction.OverwriteIfNewer, ApplyToAll: true) };

        var result = Copy([newer, older], Path.Combine(sandbox.Path, "dst") + Path.DirectorySeparatorChar, handler);

        Assert.Single(handler.Conflicts);
        Assert.Equal("src", File.ReadAllText(Path.Combine(sandbox.Path, "dst", "newer.txt")));
        Assert.Equal("dst", File.ReadAllText(Path.Combine(sandbox.Path, "dst", "older.txt")));
        Assert.Equal(1, result.Skipped);
    }

    [WindowsFact]
    public void Read_only_targets_need_a_separate_confirmation()
    {
        using var sandbox = new TestSandbox();
        var source = Write(Path.Combine(sandbox.Path, "src", "a.txt"), "new");
        var target = Write(Path.Combine(sandbox.Path, "dst", "a.txt"), "old");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        var handler = new ScriptedHandler
        {
            OnConflict = _ => new ConflictDecision(ConflictAction.Overwrite),
            OnConfirm = c => c.Kind == ConfirmationKind.OverwriteProtected ? ConfirmationAnswer.Yes : ConfirmationAnswer.Cancel,
        };

        Copy([source], Path.Combine(sandbox.Path, "dst") + Path.DirectorySeparatorChar, handler);

        Assert.Equal("new", File.ReadAllText(target));
        Assert.Single(handler.Confirmations);
    }

    [UnixFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Targets_without_write_permission_on_a_mac_need_a_separate_confirmation()
    {
        using var sandbox = new TestSandbox();
        var source = Write(Path.Combine(sandbox.Path, "src", "a.txt"), "new");
        var target = Write(Path.Combine(sandbox.Path, "dst", "a.txt"), "old");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        var handler = new ScriptedHandler
        {
            OnConflict = _ => new ConflictDecision(ConflictAction.Overwrite),
            OnConfirm = c => c.Kind == ConfirmationKind.OverwriteProtected ? ConfirmationAnswer.Yes : ConfirmationAnswer.Cancel,
        };

        Copy([source], Path.Combine(sandbox.Path, "dst") + Path.DirectorySeparatorChar, handler);

        Assert.Equal("new", File.ReadAllText(target));
        Assert.Single(handler.Confirmations);
    }

    [Fact]
    public void Existing_file_policy_skips_without_asking()
    {
        using var sandbox = new TestSandbox();
        var source = Write(Path.Combine(sandbox.Path, "src", "a.txt"), "new");
        var target = Write(Path.Combine(sandbox.Path, "dst", "a.txt"), "old");

        Copy([source], Path.Combine(sandbox.Path, "dst") + Path.DirectorySeparatorChar, new ScriptedHandler(), new CopyOptions { ExistingFiles = ExistingFilePolicy.Skip });

        Assert.Equal("old", File.ReadAllText(target));
    }

    [WindowsFact]
    public void Junctions_are_recreated_not_traversed()
    {
        using var sandbox = new TestSandbox();
        var realTarget = Directory.CreateDirectory(Path.Combine(sandbox.Path, "real")).FullName;
        Write(Path.Combine(realTarget, "inside.txt"), "x");
        var tree = Directory.CreateDirectory(Path.Combine(sandbox.Path, "tree")).FullName;
        RunCmd($"mklink /J \"{Path.Combine(tree, "link")}\" \"{realTarget}\"");

        Copy([tree], Path.Combine(sandbox.Path, "copy"), new ScriptedHandler());

        var copiedLink = new DirectoryInfo(Path.Combine(sandbox.Path, "copy", "link"));
        Assert.True(copiedLink.Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal(realTarget.TrimEnd('\\'), copiedLink.LinkTarget?.TrimEnd('\\'), ignoreCase: true);
    }

    [WindowsFact]
    public void Junction_contents_are_copied_when_asked()
    {
        using var sandbox = new TestSandbox();
        var realTarget = Directory.CreateDirectory(Path.Combine(sandbox.Path, "real")).FullName;
        Write(Path.Combine(realTarget, "inside.txt"), "x");
        var link = Path.Combine(sandbox.Path, "link");
        RunCmd($"mklink /J \"{link}\" \"{realTarget}\"");

        Copy([link], Path.Combine(sandbox.Path, "copy"), new ScriptedHandler(), new CopyOptions { CopyLinkContents = true });

        Assert.False(new DirectoryInfo(Path.Combine(sandbox.Path, "copy")).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.True(File.Exists(Path.Combine(sandbox.Path, "copy", "inside.txt")));
    }

    [Fact]
    public void Long_paths_are_copied()
    {
        using var sandbox = new TestSandbox();
        var deep = sandbox.Path;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        var file = Write(Path.Combine(deep, "long.txt"), "long");

        Copy([file], Path.Combine(deep, "copy.txt"), new ScriptedHandler());

        Assert.Equal("long", File.ReadAllText(Path.Combine(deep, "copy.txt")));
    }

    [Fact]
    public void Cancelling_mid_file_leaves_no_partial_target()
    {
        using var sandbox = new TestSandbox();
        var source = Path.Combine(sandbox.Path, "big.bin");
        using (var stream = File.Create(source))
        {
            stream.SetLength(64L * 1024 * 1024);
        }

        var target = Path.Combine(sandbox.Path, "copy.bin");
        using var control = new OperationControl();
        // The first progress call of the file comes from inside CopyFileEx: cancelling there aborts the copy mid-file.
        var handler = new ScriptedHandler { OnProgress = p => { if (p.CurrentPath == source && p.DoneBytes < p.TotalBytes) { control.Cancel(); } } };

        Assert.ThrowsAny<OperationCanceledException>(() => new CopyOperation([source], target, new CopyOptions(), false, handler, control).Run());
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Move_within_a_volume_renames_including_case_only()
    {
        using var sandbox = new TestSandbox();
        var file = Write(Path.Combine(sandbox.Path, "name.txt"), "x");
        var directory = Directory.CreateDirectory(Path.Combine(sandbox.Path, "dir")).FullName;
        Write(Path.Combine(directory, "inner.txt"), "y");

        Copy([file], Path.Combine(sandbox.Path, "NAME.txt"), new ScriptedHandler(), move: true);
        Copy([directory], Path.Combine(sandbox.Path, "moved"), new ScriptedHandler(), move: true);

        Assert.Equal("NAME.txt", Path.GetFileName(Directory.GetFiles(sandbox.Path, "name.txt").Single()));
        Assert.False(Directory.Exists(directory));
        Assert.Equal("y", File.ReadAllText(Path.Combine(sandbox.Path, "moved", "inner.txt")));
    }

    [WindowsFact]
    public void Cross_volume_move_deletes_sources_only_after_successful_copies()
    {
        using var sandbox = new TestSandbox();
        var good = Write(Path.Combine(sandbox.Path, "src", "good.txt"), "g");
        var locked = Write(Path.Combine(sandbox.Path, "src", "locked.txt"), "l");
        var handler = new ScriptedHandler { OnError = _ => ErrorAction.Skip };

        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Copy([Path.Combine(sandbox.Path, "src")], Path.Combine(sandbox.Path, "dst"), handler, move: true, forceCrossVolume: true);
        }

        Assert.False(File.Exists(good));
        Assert.True(File.Exists(Path.Combine(sandbox.Path, "dst", "good.txt")));
        Assert.True(File.Exists(locked), "a source that failed to copy must stay");
        Assert.Contains(handler.Errors, e => e.Path == locked && e.LockingProcesses?.Contains(Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Errors_can_be_retried()
    {
        using var sandbox = new TestSandbox();
        var source = Write(Path.Combine(sandbox.Path, "a.txt"), "x");
        var stream = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var handler = new ScriptedHandler
        {
            OnError = _ =>
            {
                stream.Dispose();
                return ErrorAction.Retry;
            },
        };

        var result = Copy([source], Path.Combine(sandbox.Path, "b.txt"), handler);

        Assert.Single(handler.Errors);
        Assert.Equal(1, result.Succeeded);
    }

    [Fact]
    public void Operations_outside_the_sandbox_are_blocked_by_the_guard()
    {
        using var sandbox = new TestSandbox();
        var file = Write(Path.Combine(sandbox.Path, "a.txt"), "x");
        var outside = Path.Combine(Path.GetTempPath(), "habi-guard-" + Guid.NewGuid().ToString("N") + ".txt");

        Assert.Throws<InvalidOperationException>(() => Copy([file], outside, new ScriptedHandler()));
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public void Progress_reaches_the_total()
    {
        using var sandbox = new TestSandbox();
        var a = Write(Path.Combine(sandbox.Path, "src", "a.txt"), new string('a', 1000));
        var handler = new ScriptedHandler();

        Copy([a], Path.Combine(sandbox.Path, "b.txt"), handler);

        Assert.Equal(1000, handler.LastProgress!.TotalBytes);
        Assert.Equal(handler.LastProgress.TotalBytes, handler.LastProgress.DoneBytes);
    }

    internal static void RunCmd(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c {arguments}") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
