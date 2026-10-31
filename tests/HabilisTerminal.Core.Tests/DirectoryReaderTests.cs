using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using HabilisTerminal.Core.FileSystem;

namespace HabilisTerminal.Core.Tests;

public class DirectoryReaderTests
{
    [Fact]
    public void Reads_files_and_directories_with_attributes_and_sizes()
    {
        using var sandbox = new TestSandbox();
        File.WriteAllText(Path.Combine(sandbox.Path, "a.txt"), "12345");
        Directory.CreateDirectory(Path.Combine(sandbox.Path, "sub"));
        var hidden = Path.Combine(sandbox.Path, "hidden.dat");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var entries = DirectoryReader.ReadAll(sandbox.Path).ToDictionary(e => e.Name);

        Assert.Equal(3, entries.Count);
        Assert.Equal(5, entries["a.txt"].Length);
        Assert.False(entries["a.txt"].IsDirectory);
        Assert.True(entries["sub"].IsDirectory);
        Assert.Equal(0, entries["sub"].Length);
        Assert.True(entries["hidden.dat"].IsHidden);
        Assert.Equal("txt", entries["a.txt"].Extension.ToString());
    }

    [Fact]
    public void First_batch_is_small_and_later_batches_are_large()
    {
        using var sandbox = new TestSandbox();
        for (var i = 0; i < 25; i++)
        {
            File.WriteAllText(Path.Combine(sandbox.Path, $"f{i}.txt"), string.Empty);
        }

        var batches = DirectoryReader.ReadBatches(sandbox.Path, firstBatchSize: 5, batchSize: 10).ToList();

        Assert.Equal([5, 10, 10], batches.Select(b => b.Length));
    }

    [Fact]
    public void Missing_directory_is_reported_as_not_found()
    {
        using var sandbox = new TestSandbox();

        var ex = Assert.Throws<DirectoryReadException>(() => DirectoryReader.ReadAll(Path.Combine(sandbox.Path, "missing")));

        Assert.Equal(DirectoryReadError.NotFound, ex.Error);
    }

    [Fact]
    public void Directory_without_list_permission_is_reported_as_access_denied()
    {
        using var sandbox = new TestSandbox();
        var locked = Directory.CreateDirectory(Path.Combine(sandbox.Path, "locked"));
        var user = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = locked.GetAccessControl();
        security.AddAccessRule(deny);
        locked.SetAccessControl(security);
        try
        {
            var ex = Assert.Throws<DirectoryReadException>(() => DirectoryReader.ReadAll(locked.FullName));

            Assert.Equal(DirectoryReadError.AccessDenied, ex.Error);
        }
        finally
        {
            security.RemoveAccessRule(deny);
            locked.SetAccessControl(security);
        }
    }

    [Fact]
    public void Reads_directories_beyond_max_path()
    {
        using var sandbox = new TestSandbox();
        var deep = sandbox.Path;
        while (deep.Length < 320)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "deep.txt"), "x");

        var entries = DirectoryReader.ReadAll(deep);

        Assert.Single(entries, e => e.Name == "deep.txt");
    }

    [Fact]
    public void Junctions_are_listed_as_reparse_points_and_not_traversed()
    {
        using var sandbox = new TestSandbox();
        var target = Directory.CreateDirectory(Path.Combine(sandbox.Path, "target")).FullName;
        File.WriteAllText(Path.Combine(target, "inside.txt"), "x");
        var junction = Path.Combine(sandbox.Path, "junction");
        RunCmd($"mklink /J \"{junction}\" \"{target}\"");

        var entries = DirectoryReader.ReadAll(sandbox.Path).ToDictionary(e => e.Name);

        Assert.Equal(2, entries.Count);
        Assert.True(entries["junction"].IsDirectory);
        Assert.True(entries["junction"].IsReparsePoint);
        Assert.False(entries["target"].IsReparsePoint);
    }

    [Fact]
    public void File_symlinks_are_listed_as_reparse_points()
    {
        using var sandbox = new TestSandbox();
        var target = Path.Combine(sandbox.Path, "target.txt");
        File.WriteAllText(target, "x");
        try
        {
            File.CreateSymbolicLink(Path.Combine(sandbox.Path, "link.txt"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating symbolic links needs administrator rights or Developer Mode.");
        }

        var link = DirectoryReader.ReadAll(sandbox.Path).Single(e => e.Name == "link.txt");

        Assert.True(link.IsReparsePoint);
        Assert.False(link.IsDirectory);
    }

    [Fact]
    public void Read_entry_returns_current_state_or_null()
    {
        using var sandbox = new TestSandbox();
        var file = Path.Combine(sandbox.Path, "one.txt");
        File.WriteAllText(file, "abc");
        Directory.CreateDirectory(Path.Combine(sandbox.Path, "dir"));

        Assert.Equal(3, DirectoryReader.ReadEntry(sandbox.Path, "one.txt")!.Value.Length);
        Assert.True(DirectoryReader.ReadEntry(sandbox.Path, "dir")!.Value.IsDirectory);
        File.Delete(file);
        Assert.Null(DirectoryReader.ReadEntry(sandbox.Path, "one.txt"));
    }

    [Fact]
    public void Enumeration_can_be_cancelled()
    {
        using var sandbox = new TestSandbox();
        File.WriteAllText(Path.Combine(sandbox.Path, "a.txt"), string.Empty);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => DirectoryReader.ReadAll(sandbox.Path, cancellation.Token));
    }

    private static void RunCmd(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c {arguments}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
