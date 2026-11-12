using System.ComponentModel;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Interop;
using HabilisTerminal.Core.Platform.Portable;

namespace HabilisTerminal.Core.Tests;

/// <summary>The portable file calls (macOS and Linux, #259), run here on Windows too: they use only .NET.</summary>
public sealed class PortablePlatformTests
{
    private readonly PortableFileSystem _files = new();

    [Fact]
    public void Copy_reports_progress_and_keeps_the_time()
    {
        using var sandbox = new TestSandbox();
        var source = Path.Combine(sandbox.Path, "a.bin");
        File.WriteAllBytes(source, new byte[3 * 1024 * 1024 + 5]);
        var stamp = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, stamp);
        var target = Path.Combine(sandbox.Path, "b.bin");
        var seen = new List<(long Done, long Total)>();

        _files.CopyFile(source, target, copyLinkItself: false, failIfExists: true, (done, total) =>
        {
            seen.Add((done, total));
            return true;
        });

        Assert.Equal(new FileInfo(source).Length, new FileInfo(target).Length);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(target));
        Assert.True(seen.Count > 2);
        Assert.Equal((new FileInfo(source).Length, new FileInfo(source).Length), seen[^1]);
    }

    [Fact]
    public void Cancelled_copy_leaves_no_partial_file()
    {
        using var sandbox = new TestSandbox();
        var source = Path.Combine(sandbox.Path, "a.bin");
        File.WriteAllBytes(source, new byte[4 * 1024 * 1024]);
        var target = Path.Combine(sandbox.Path, "b.bin");

        var error = Assert.Throws<Win32Exception>(() =>
            _files.CopyFile(source, target, copyLinkItself: false, failIfExists: false, (done, _) => done < 1024 * 1024));

        Assert.Equal(NativeFile.ErrorRequestAborted, error.NativeErrorCode);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Existing_targets_come_back_with_the_windows_codes()
    {
        using var sandbox = new TestSandbox();
        var a = Path.Combine(sandbox.Path, "a.txt");
        var b = Path.Combine(sandbox.Path, "b.txt");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");

        Assert.Equal(NativeFile.ErrorFileExists,
            Assert.Throws<Win32Exception>(() => _files.CopyFile(a, b, false, failIfExists: true, (_, _) => true)).NativeErrorCode);
        Assert.Equal(NativeFile.ErrorAlreadyExists, Assert.Throws<Win32Exception>(() => _files.Move(a, b)).NativeErrorCode);
    }

    [Fact]
    public void Move_renames_files_and_folders_and_changes_case()
    {
        using var sandbox = new TestSandbox();
        var file = Path.Combine(sandbox.Path, "name.txt");
        File.WriteAllText(file, "x");
        _files.Move(file, Path.Combine(sandbox.Path, "Name.txt"));
        Assert.Equal("Name.txt", Path.GetFileName(Directory.GetFiles(sandbox.Path).Single()));

        var folder = Directory.CreateDirectory(Path.Combine(sandbox.Path, "dir")).FullName;
        _files.Move(folder, Path.Combine(sandbox.Path, "moved"));
        Assert.True(Directory.Exists(Path.Combine(sandbox.Path, "moved")));
    }

    [Fact]
    public void A_path_is_on_the_volume_of_its_root()
    {
        using var sandbox = new TestSandbox();
        var volume = _files.VolumeOf(sandbox.Path);

        Assert.StartsWith(volume.TrimEnd(Path.DirectorySeparatorChar), sandbox.Path, StringComparison.OrdinalIgnoreCase);
        Assert.True(_files.FreeBytes(sandbox.Path) > 0);
        Assert.Equal(0u, _files.ReparseTag(sandbox.Path));
        Assert.Null(_files.ReadReparseData(sandbox.Path));
    }

    [Fact]
    public void Symbolic_links_read_like_windows_reparse_data()
    {
        using var sandbox = new TestSandbox();
        var target = Directory.CreateDirectory(Path.Combine(sandbox.Path, "target")).FullName;
        var link = Path.Combine(sandbox.Path, "link");
        try
        {
            _files.CreateJunction(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows without Developer Mode cannot make symbolic links; macOS and Linux always can.
            Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need Developer Mode here");
            throw;
        }

        Assert.Equal(NativeFile.ReparseTagSymlink, _files.ReparseTag(link));
        var (tag, stored, relative) = LinkInfo.Parse(_files.ReadReparseData(link)!);
        Assert.Equal(NativeFile.ReparseTagSymlink, tag);
        Assert.Equal(target, stored);
        Assert.False(relative);
    }

    [Fact]
    public void Compression_and_encryption_are_not_offered()
    {
        Assert.False(_files.HasCompressionAndEncryption);
        Assert.Throws<PlatformNotSupportedException>(() => _files.SetCompression("x", true));
    }
}
