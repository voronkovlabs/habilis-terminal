using HabilisTerminal.Core.Operations;

namespace HabilisTerminal.Core.Tests;

public class RenameItemTests
{
    [Theory]
    [InlineData("report.txt", false, 6)]
    [InlineData("archive.tar.gz", false, 11)]
    [InlineData("noextension", false, 11)]
    [InlineData(".gitignore", false, 10)]
    [InlineData("my.folder", true, 9)]
    public void The_name_is_selected_without_its_extension(string name, bool isFolder, int length) =>
        Assert.Equal(length, RenameItem.EditableLength(name, isFolder));

    [Fact]
    public void A_file_and_a_folder_get_their_new_names_with_their_content()
    {
        using var sandbox = new TestSandbox();
        File.WriteAllText(Path.Combine(sandbox.Path, "a.txt"), "text");
        Directory.CreateDirectory(Path.Combine(sandbox.Path, "dir"));
        File.WriteAllText(Path.Combine(sandbox.Path, "dir", "inside.txt"), "x");

        Assert.True(RenameItem.Rename(sandbox.Path, "a.txt", "  b.txt "));
        Assert.True(RenameItem.Rename(sandbox.Path, "dir", "dir2"));

        Assert.Equal("text", File.ReadAllText(Path.Combine(sandbox.Path, "b.txt")));
        Assert.False(File.Exists(Path.Combine(sandbox.Path, "a.txt")));
        Assert.True(File.Exists(Path.Combine(sandbox.Path, "dir2", "inside.txt")));
    }

    [Fact]
    public void The_same_name_does_nothing_and_a_change_of_case_is_a_rename()
    {
        using var sandbox = new TestSandbox();
        File.WriteAllText(Path.Combine(sandbox.Path, "a.txt"), "text");

        Assert.False(RenameItem.Rename(sandbox.Path, "a.txt", "a.txt"));
        Assert.True(RenameItem.Rename(sandbox.Path, "a.txt", "A.txt"));
        Assert.Equal("A.txt", Path.GetFileName(Directory.GetFiles(sandbox.Path).Single()));
    }

    [Theory]
    [InlineData("taken.txt")]
    [InlineData("taken")]
    public void What_exists_is_never_overwritten(string name)
    {
        using var sandbox = new TestSandbox();
        File.WriteAllText(Path.Combine(sandbox.Path, "a.txt"), "mine");
        File.WriteAllText(Path.Combine(sandbox.Path, "taken.txt"), "theirs");
        Directory.CreateDirectory(Path.Combine(sandbox.Path, "taken"));

        var error = Assert.Throws<IOException>(() => RenameItem.Rename(sandbox.Path, "a.txt", name));

        Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(sandbox.Path, "taken.txt")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(sandbox.Path, "a.txt")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b.txt")]
    [InlineData(@"..\b.txt")]
    [InlineData("CON")]
    [InlineData("name.")]
    public void A_bad_name_is_refused_and_says_why(string name)
    {
        using var sandbox = new TestSandbox();
        File.WriteAllText(Path.Combine(sandbox.Path, "a.txt"), "x");
        var error = Assert.Throws<ArgumentException>(() => RenameItem.Rename(sandbox.Path, "a.txt", name));
        Assert.NotEmpty(error.Message);
        Assert.True(File.Exists(Path.Combine(sandbox.Path, "a.txt")));
    }
}
