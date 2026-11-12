using System.Diagnostics;
using System.Text;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Interop;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.Core.Tests;

public sealed class LinkInfoTests : IDisposable
{
    private readonly TestSandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string In(string name) => Path.Combine(_sandbox.Path, name);

    private string Folder(string name, int fileBytes = 0)
    {
        var folder = In(name);
        Directory.CreateDirectory(folder);
        if (fileBytes > 0)
        {
            File.WriteAllBytes(Path.Combine(folder, "data.bin"), new byte[fileBytes]);
        }

        return folder;
    }

    private static void Run(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd", "/c " + arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit();
    }

    [Theory]
    [InlineData(0xA0000003u, LinkKind.Junction)]
    [InlineData(0xA000000Cu, LinkKind.Symlink)]
    [InlineData(0xA000001Du, LinkKind.WslLink)]
    [InlineData(0x8000001Bu, LinkKind.AppAlias)]
    [InlineData(0xA0001234u, LinkKind.Other)]
    [InlineData(0x9000701Au, LinkKind.None)] // a OneDrive folder: it holds its files here
    [InlineData(0x80000013u, LinkKind.None)] // deduplication
    [InlineData(0u, LinkKind.None)]
    public void Tags_tell_links_from_what_holds_its_own_data(uint tag, LinkKind kind) =>
        Assert.Equal(kind, LinkInfo.KindOf(tag));

    [Theory]
    [InlineData(LinkKind.Junction, "Junction")]
    [InlineData(LinkKind.MountPoint, "Mount point")]
    [InlineData(LinkKind.Symlink, "Symlink")]
    [InlineData(LinkKind.WslLink, "WSL link")]
    [InlineData(LinkKind.AppAlias, "App alias")]
    [InlineData(LinkKind.Other, "Link")]
    public void Each_kind_has_its_name(LinkKind kind, string name) => Assert.Equal(name, LinkInfo.Name(kind));

    private static byte[] Surrogate(uint tag, int headerExtra, string substitute, string print, uint flags = 0)
    {
        var sub = Encoding.Unicode.GetBytes(substitute);
        var printed = Encoding.Unicode.GetBytes(print);
        var data = new byte[16 + headerExtra + sub.Length + printed.Length];
        BitConverter.GetBytes(tag).CopyTo(data, 0);
        BitConverter.GetBytes((ushort)sub.Length).CopyTo(data, 10);
        BitConverter.GetBytes((ushort)sub.Length).CopyTo(data, 12);
        BitConverter.GetBytes((ushort)printed.Length).CopyTo(data, 14);
        if (headerExtra > 0)
        {
            BitConverter.GetBytes(flags).CopyTo(data, 16);
        }

        sub.CopyTo(data, 16 + headerExtra);
        printed.CopyTo(data, 16 + headerExtra + sub.Length);
        return data;
    }

    [Fact]
    public void A_symlink_buffer_gives_its_target_and_whether_it_is_relative()
    {
        Assert.Equal((0xA000000Cu, @"..\there", true), LinkInfo.Parse(Surrogate(0xA000000C, 4, @"..\there", @"..\there", flags: 1)));
        Assert.Equal((0xA000000Cu, @"C:\there", false), LinkInfo.Parse(Surrogate(0xA000000C, 4, @"\??\C:\there", @"C:\there")));
    }

    [Fact]
    public void A_mount_point_of_a_volume_keeps_the_name_of_the_volume()
    {
        var parsed = LinkInfo.Parse(Surrogate(0xA0000003, 0, @"\??\Volume{1234}\", string.Empty));

        Assert.Equal(@"\??\Volume{1234}\", parsed.Target);
    }

    [Fact]
    public void A_wsl_link_buffer_gives_its_linux_target()
    {
        var text = Encoding.UTF8.GetBytes("/home/me");
        var data = new byte[12 + text.Length];
        BitConverter.GetBytes(0xA000001Du).CopyTo(data, 0);
        BitConverter.GetBytes((ushort)(4 + text.Length)).CopyTo(data, 4);
        text.CopyTo(data, 12);

        Assert.Equal("/home/me", LinkInfo.Parse(data).Target);
    }

    [Fact]
    public void An_app_alias_buffer_gives_the_program()
    {
        var text = Encoding.Unicode.GetBytes("Package\0App\0C:\\Program Files\\WindowsApps\\p\\app.exe\0" + "0\0");
        var data = new byte[12 + text.Length];
        BitConverter.GetBytes(0x8000001Bu).CopyTo(data, 0);
        text.CopyTo(data, 12);

        Assert.Equal(@"C:\Program Files\WindowsApps\p\app.exe", LinkInfo.Parse(data).Target);
    }

    [Fact]
    public void A_buffer_too_short_or_of_an_unknown_kind_has_no_target()
    {
        Assert.Null(LinkInfo.Parse([1, 2]).Target);
        Assert.Null(LinkInfo.Parse(Surrogate(0xA0001234, 0, "x", "x")).Target);
    }

    [Fact]
    public void A_junction_tells_where_it_leads()
    {
        var target = Folder("target");
        var link = In("link");
        NativeFile.CreateJunction(link, target);

        var info = LinkInfo.Read(link)!;

        Assert.Equal(LinkKind.Junction, info.Kind);
        Assert.Equal(target, info.StoredTarget);
        Assert.Equal(target, info.FullTarget);
        Assert.Null(info.FinalTarget);
        Assert.False(info.IsRelative);
        Assert.True(info.TargetExists);
        Assert.True(info.TargetIsFolder);
    }

    [Fact]
    public void A_link_to_a_link_tells_where_it_ends()
    {
        var target = Folder("target");
        var middle = In("middle");
        NativeFile.CreateJunction(middle, target);
        var link = In("link");
        NativeFile.CreateJunction(link, middle);

        var info = LinkInfo.Read(link)!;

        Assert.Equal(middle, info.FullTarget);
        Assert.Equal(target, info.FinalTarget);
    }

    [Fact]
    public void A_broken_link_says_so()
    {
        var target = Folder("target");
        var link = In("link");
        NativeFile.CreateJunction(link, target);
        Directory.Delete(target);

        var info = LinkInfo.Read(link)!;

        Assert.Equal(target, info.StoredTarget);
        Assert.False(info.TargetExists);
        Assert.Null(info.TargetIsFolder);
    }

    [WindowsFact]
    public void A_listing_names_the_kind_of_each_link()
    {
        Folder("plain");
        NativeFile.CreateJunction(In("link"), Folder("target"));
        File.WriteAllText(In("file.txt"), "x");

        var entries = Directory.Exists(_sandbox.Path) ? HabilisTerminal.Core.FileSystem.DirectoryReader.ReadAll(_sandbox.Path).ToDictionary(e => e.Name) : [];

        Assert.Equal(LinkKind.Junction, entries["link"].Link);
        Assert.True(entries["link"].IsLink);
        Assert.Equal(LinkKind.None, entries["plain"].Link);
        Assert.False(entries["file.txt"].IsLink);
        Assert.Equal(LinkKind.Junction, HabilisTerminal.Core.FileSystem.DirectoryReader.ReadEntry(_sandbox.Path, "link")!.Value.Link);
    }

    [Fact]
    public void An_ordinary_file_has_nothing_to_show() =>
        Assert.Null(LinkInfo.Read(WriteFile("plain.txt")));

    private string WriteFile(string name)
    {
        File.WriteAllText(In(name), "text");
        return In(name);
    }

    [WindowsFact]
    public void A_file_with_several_names_lists_the_others()
    {
        var file = WriteFile("first.txt");
        var other = In("second.txt");
        Run($"mklink /H \"{other}\" \"{file}\"");

        var info = LinkInfo.Read(file)!;

        Assert.Equal(LinkKind.None, info.Kind);
        Assert.Equal(2, info.HardLinks);
        Assert.Equal(2, LinkInfo.HardLinkCount(file));
        Assert.Equal(1, LinkInfo.HardLinkCount(Folder("plain")));
        Assert.Equal([other], info.OtherNames);
    }

    [Fact]
    public void Links_do_not_count_in_a_folders_size()
    {
        var root = Folder("root", fileBytes: 100);
        var outside = Folder("outside", fileBytes: 5000);
        NativeFile.CreateJunction(Path.Combine(root, "link"), outside);
        Directory.CreateDirectory(Path.Combine(root, "real"));

        var totals = DirectorySize.Compute(root);

        Assert.Equal(100, totals.Bytes);
        Assert.Equal(1, totals.Files);
        Assert.Equal(1, totals.Folders); // "real", not the link
    }

    [Fact]
    public void A_link_counted_by_itself_is_nothing()
    {
        var outside = Folder("outside", fileBytes: 5000);
        var link = In("link");
        NativeFile.CreateJunction(link, outside);

        Assert.Equal(new DirectorySize.Result(0, 0, 0, 0), DirectorySize.Compute(link));
    }
}
