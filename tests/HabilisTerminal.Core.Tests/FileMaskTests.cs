using HabilisTerminal.Core.FileSystem;

namespace HabilisTerminal.Core.Tests;

public class FileMaskTests
{
    [Theory]
    [InlineData("*", "anything.txt", true)]
    [InlineData("*.*", "noext", true)]
    [InlineData("*.txt", "Notes.TXT", true)]
    [InlineData("*.txt", "notes.txt.bak", false)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    [InlineData("[a-c]*", "beta", true)]
    [InlineData("[a-c]*", "delta", false)]
    [InlineData("*.", "readme", true)]
    [InlineData("*.", "readme.md", false)]
    [InlineData("*.cs,*.xaml", "View.xaml", true)]
    [InlineData("*.cs;*.xaml", "Program.cs", true)]
    [InlineData("*.cs|*.g.cs", "Program.cs", true)]
    [InlineData("*.cs|*.g.cs", "Program.g.cs", false)]
    [InlineData("|*.tmp", "keep.txt", true)]
    [InlineData("|*.tmp", "drop.tmp", false)]
    [InlineData("\"my file,1.txt\"", "my file,1.txt", true)]
    [InlineData("/^\\d+\\.log$/", "2026.log", true)]
    [InlineData("/^\\d+\\.log$/", "app.log", false)]
    [InlineData("/^readme$/i", "README", true)]
    [InlineData("/a,b/", "a,b", true)]
    public void Matches_like_far(string mask, string name, bool expected)
    {
        Assert.Equal(expected, FileMask.Parse(mask).IsMatch(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"unbalanced")]
    [InlineData("[abc")]
    [InlineData("/(/")]
    [InlineData("/x/q")]
    public void Rejects_invalid_masks(string mask)
    {
        Assert.Throws<FormatException>(() => FileMask.Parse(mask));
        Assert.False(FileMask.TryParse(mask, out _));
    }
}
