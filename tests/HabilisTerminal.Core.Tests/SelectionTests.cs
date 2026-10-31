using HabilisTerminal.Core.FileSystem;

namespace HabilisTerminal.Core.Tests;

public class SelectionTests
{
    private static readonly DateTime Day = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly FileEntry[] Entries =
    [
        new("a.txt", FileAttributes.Normal, 10, Day, Day, Day),
        new("b.txt", FileAttributes.Normal, 20, Day, Day, Day),
        new("c.log", FileAttributes.Normal, 30, Day, Day, Day),
        new("dir.txt", FileAttributes.Directory, 0, Day, Day, Day),
    ];

    [Fact]
    public void Toggle_selects_and_deselects()
    {
        var selection = new Selection();

        Assert.True(selection.Toggle("a.txt"));
        Assert.True(selection.Contains("A.TXT"));
        Assert.False(selection.Toggle("a.txt"));
        Assert.Equal(0, selection.Count);
    }

    [Fact]
    public void Mask_selection_skips_directories_unless_asked()
    {
        var selection = new Selection();

        Assert.Equal(2, selection.SelectByMask(Entries, FileMask.Parse("*.txt")));
        Assert.False(selection.Contains("dir.txt"));
        Assert.Equal(1, selection.SelectByMask(Entries, FileMask.Parse("*.txt"), includeDirectories: true));
        Assert.Equal(1, selection.DeselectByMask(Entries, FileMask.Parse("a*")));
        Assert.Equal(["b.txt", "dir.txt"], selection.Names.Order());
    }

    [Fact]
    public void Invert_flips_files_only_by_default()
    {
        var selection = new Selection();
        selection.Toggle("a.txt");

        selection.Invert(Entries);

        Assert.Equal(["b.txt", "c.log"], selection.Names.Order());
    }

    [Fact]
    public void Summary_and_retain_existing()
    {
        var selection = new Selection();
        selection.SelectByMask(Entries, FileMask.All, includeDirectories: true);
        selection.Toggle("gone.txt");

        selection.RetainExisting(Entries);

        Assert.Equal((3, 1, 60L), selection.Summarize(Entries));
        Assert.False(selection.Contains("gone.txt"));
    }
}
