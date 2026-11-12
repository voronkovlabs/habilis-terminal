using System.Text;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Interop;
using HabilisTerminal.Core.Search;

namespace HabilisTerminal.Core.Tests;

public sealed class FileSearchTests : IDisposable
{
    private readonly TestSandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string Write(string relative, string text, Encoding? encoding = null)
    {
        // The cases write Windows paths; elsewhere the separator is the system's (#259).
        var path = Path.Combine(_sandbox.Path, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        return path;
    }

    private List<string> Find(string mask, string? text = null, bool matchCase = false, bool wholeWords = false, bool folders = false, bool recursive = true, string? root = null)
    {
        var found = new List<string>();
        FileSearch.Run(new FileSearchRequest(FileMask.Parse(mask), text, matchCase, wholeWords, folders, [root ?? _sandbox.Path], recursive),
            item => found.Add(Path.GetRelativePath(_sandbox.Path, item.Path).Replace(Path.DirectorySeparatorChar, '\\')));
        return found;
    }

    [Fact]
    public void Masks_find_files_in_all_subfolders_in_listing_order()
    {
        Write("b.cs", "x");
        Write("a.txt", "x");
        Write(@"sub\c.cs", "x");
        Write(@"sub\deep\d.cs", "x");
        Write(@"obj\e.cs", "x");

        Assert.Equal(["b.cs", @"obj\e.cs", @"sub\c.cs", @"sub\deep\d.cs"], Find("*.cs"));
        Assert.Equal([@"b.cs", @"sub\c.cs", @"sub\deep\d.cs"], Find(@"*.cs|obj\*"[..4] + "|e.cs"));
    }

    [Fact]
    public void The_current_folder_only_does_not_go_down()
    {
        Write("a.cs", "x");
        Write(@"sub\b.cs", "x");

        Assert.Equal(["a.cs"], Find("*.cs", recursive: false));
    }

    [Fact]
    public void Folders_are_found_only_when_asked_and_only_without_text()
    {
        Write(@"logs\a.txt", "x");
        Write(@"old logs\b.txt", "x");

        Assert.Empty(Find("*logs*"));
        Assert.Equal(["logs", "old logs"], Find("*logs*", folders: true));
        Assert.Empty(Find("*logs*", text: "x", folders: true));
    }

    [Fact]
    public void Text_is_found_regardless_of_case_unless_asked()
    {
        Write("a.txt", "Hello World");
        Write("b.txt", "nothing here");

        Assert.Equal(["a.txt"], Find("*", "hello world"));
        Assert.Empty(Find("*", "hello world", matchCase: true));
        Assert.Equal(["a.txt"], Find("*", "Hello", matchCase: true));
    }

    [Fact]
    public void Cyrillic_text_is_found_in_any_case()
    {
        Write("a.txt", "Привет, Мир");

        Assert.Equal(["a.txt"], Find("*", "привет, мир"));
    }

    [Fact]
    public void Whole_words_do_not_match_inside_other_words()
    {
        Write("a.txt", "a cat sat");
        Write("b.txt", "concatenate");
        Write("c.txt", "cat_like");

        Assert.Equal(["a.txt", "b.txt", "c.txt"], Find("*", "cat"));
        Assert.Equal(["a.txt"], Find("*", "cat", wholeWords: true));
    }

    [Fact]
    public void Encodings_are_told_by_the_file()
    {
        Write("utf16.txt", "Найди меня", Encoding.Unicode);
        Write("utf16be.txt", "Найди меня", Encoding.BigEndianUnicode);
        Write("utf8.txt", "Найди меня", Encoding.UTF8);

        Assert.Equal(["utf16.txt", "utf16be.txt", "utf8.txt"], Find("*", "найди"));
    }

    [Fact]
    public void A_binary_file_is_searched_for_ansi_and_utf16_text()
    {
        var path = Path.Combine(_sandbox.Path, "program.bin");
        var bytes = new List<byte> { 0, 1, 2, 0, 0, 255 };
        bytes.AddRange(Encoding.ASCII.GetBytes("VersionInfo"));
        bytes.AddRange(new byte[] { 0, 0, 7 });
        bytes.AddRange(Encoding.Unicode.GetBytes("ProductName"));
        File.WriteAllBytes(path, [.. bytes]);

        Assert.Equal(["program.bin"], Find("*", "versioninfo"));
        Assert.Equal(["program.bin"], Find("*", "productname"));
        Assert.Empty(Find("*", "missing"));
    }

    [Fact]
    public void A_match_across_two_read_chunks_is_found()
    {
        var finder = new TextFinder("needle", matchCase: false, wholeWords: true);
        var text = new string('x', (1 << 20) - 3) + " needle end";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        Assert.True(finder.IsIn(stream, Encoding.UTF8));
        using var glued = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', (1 << 20) - 3) + " needlework"));
        Assert.False(finder.IsIn(glued, Encoding.UTF8));
    }

    [Fact]
    public void Links_are_not_walked()
    {
        var outside = Path.Combine(_sandbox.Path, "outside");
        Write(@"outside\far.cs", "x");
        var root = Path.Combine(_sandbox.Path, "root");
        Write(@"root\near.cs", "x");
        NativeFile.CreateJunction(Path.Combine(root, "link"), outside);

        Assert.Equal([@"root\near.cs"], Find("*.cs", root: root));
    }

    [Fact]
    public void Cancelling_stops_the_walk()
    {
        for (var i = 0; i < 50; i++)
        {
            Write($@"f{i:d2}\a.cs", "x");
        }

        using var cancel = new CancellationTokenSource();
        var found = 0;
        Assert.Throws<OperationCanceledException>(() => FileSearch.Run(
            new FileSearchRequest(FileMask.Parse("*.cs"), null, false, false, false, [_sandbox.Path], true),
            _ =>
            {
                if (++found == 3)
                {
                    cancel.Cancel();
                }
            },
            cancellationToken: cancel.Token));
        Assert.Equal(3, found);
    }

    [Fact]
    public void Scopes_give_their_roots()
    {
        var current = Path.Combine(_sandbox.Path, "here");

        Assert.Equal([current], FileSearch.RootsFor(SearchScope.FromCurrentFolder, current, []));
        Assert.Equal([Path.GetPathRoot(current)!], FileSearch.RootsFor(SearchScope.FromDriveRoot, current, []));
        Assert.Equal(["x", "y"], FileSearch.RootsFor(SearchScope.SelectedFolders, current, ["x", "y"]));
        Assert.Equal([current], FileSearch.RootsFor(SearchScope.SelectedFolders, current, []));
        Assert.Contains(FileSearch.RootsFor(SearchScope.AllLocalDrives, current, []), r => string.Equals(r, Path.GetPathRoot(current), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unreadable_root_is_counted_not_thrown()
    {
        var count = FileSearch.Run(new FileSearchRequest(FileMask.All, null, false, false, false, [Path.Combine(_sandbox.Path, "missing")], true), _ => { });

        Assert.Equal(1, count);
    }
}
