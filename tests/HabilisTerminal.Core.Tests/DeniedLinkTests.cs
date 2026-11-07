using System.Diagnostics;
using HabilisTerminal.Core.FileSystem;

namespace HabilisTerminal.Core.Tests;

public sealed class DeniedLinkTests : IDisposable
{
    private readonly TestSandbox _sandbox = new();
    private readonly List<string> _denied = [];

    public void Dispose()
    {
        foreach (var path in _denied)
        {
            Run("icacls", $"\"{path}\" /remove:d *S-1-1-0");
        }

        _sandbox.Dispose();
    }

    private static void Run(string program, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit();
    }

    private string Target()
    {
        var target = Path.Combine(_sandbox.Path, "target");
        Directory.CreateDirectory(Path.Combine(target, "inner"));
        File.WriteAllText(Path.Combine(target, "a.txt"), "a");
        return target;
    }

    private string Junction(string name, string target)
    {
        var link = Path.Combine(_sandbox.Path, name);
        Run("cmd", $"/c mklink /J \"{link}\" \"{target}\"");
        return link;
    }

    private void DenyListing(string path)
    {
        _denied.Add(path);
        Run("icacls", $"\"{path}\" /deny *S-1-1-0:(RD)");
    }

    [Fact]
    public void A_junction_that_denies_listing_shows_what_its_target_holds()
    {
        var link = Junction("link", Target());
        DenyListing(link);
        Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(link));

        var names = DirectoryReader.ReadAll(link).Select(e => e.Name).Order(StringComparer.Ordinal);

        Assert.Equal(["a.txt", "inner"], names);
    }

    [Fact]
    public void What_is_inside_opens_through_the_link()
    {
        var link = Junction("link", Target());
        DenyListing(link);

        Assert.Empty(DirectoryReader.ReadAll(Path.Combine(link, "inner")));
    }

    [Fact]
    public void An_ordinary_folder_that_denies_listing_is_still_denied()
    {
        var folder = Path.Combine(_sandbox.Path, "closed");
        Directory.CreateDirectory(folder);
        DenyListing(folder);

        var error = Assert.Throws<DirectoryReadException>(() => DirectoryReader.ReadAll(folder));

        Assert.Equal(DirectoryReadError.AccessDenied, error.Error);
    }

    [Fact]
    public void A_link_whose_target_is_denied_too_is_denied()
    {
        var target = Target();
        var link = Junction("link", target);
        DenyListing(link);
        DenyListing(target);

        var error = Assert.Throws<DirectoryReadException>(() => DirectoryReader.ReadAll(link));

        Assert.Equal(DirectoryReadError.AccessDenied, error.Error);
        Assert.Equal(link, error.Path);
    }

    [Fact]
    public void A_denied_link_with_no_target_left_is_denied()
    {
        var target = Target();
        var link = Junction("link", target);
        DenyListing(link);
        Directory.Delete(target, recursive: true);

        var error = Assert.Throws<DirectoryReadException>(() => DirectoryReader.ReadAll(link));

        Assert.Equal(DirectoryReadError.AccessDenied, error.Error);
    }
}
