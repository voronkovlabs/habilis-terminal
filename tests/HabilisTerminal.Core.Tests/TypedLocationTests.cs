using HabilisTerminal.Core.Panels;

namespace HabilisTerminal.Core.Tests;

public class TypedLocationTests
{
    [Theory]
    [InlineData(@"  C:\Work  ", @"C:\Work")]
    [InlineData(@"""C:\Program Files\a.txt""", @"C:\Program Files\a.txt")]
    [InlineData("'s3://b/a/'", "s3://b/a/")]
    [InlineData("\"", "\"")]
    [InlineData("\"half", "\"half")]
    public void Spaces_and_quotes_go(string text, string expected) => Assert.Equal(expected, TypedLocation.Clean(text));

    [Theory]
    [InlineData(@"C:\Work\a.txt", @"C:\Work\a.txt")]
    [InlineData(@"""C:\Program Files\a.txt""", @"C:\Program Files\a.txt")]
    [InlineData("sub", @"C:\Here\sub")]
    [InlineData("sub/a b.txt", @"C:\Here\sub\a b.txt")]
    [InlineData(@"..\x", @"C:\x")]
    [InlineData("s3://bucket/a/", "s3://bucket/a/")]
    public void Goto_takes_full_paths_addresses_and_what_is_relative_to_the_shell(string text, string expected) =>
        Assert.Equal(expected, TypedLocation.ForGoto(text, @"C:\Here", t => t.StartsWith("s3:", StringComparison.Ordinal)));

    [Fact]
    public void Goto_knows_the_home_folder_and_variables()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home, TypedLocation.ForGoto("~", @"C:\Here", _ => false));
        Assert.Equal(Path.Combine(home, "docs"), TypedLocation.ForGoto(@"~\docs", @"C:\Here", _ => false));
        Assert.Equal(Path.Combine(home, "a.txt"), TypedLocation.ForGoto(@"%USERPROFILE%\a.txt", @"C:\Here", _ => false));
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:\Work\a.txt", true)]
    [InlineData("C:/Work/a.txt", true)]
    [InlineData(@"\\server\share", true)]
    [InlineData("C:", false)]
    [InlineData("C", false)]
    [InlineData("Work", false)]
    [InlineData(@"Work\a", false)]
    [InlineData("s3://bucket/a/", false)]
    public void Only_a_full_path_counts(string text, bool expected) => Assert.Equal(expected, TypedLocation.IsPath(text));
}
