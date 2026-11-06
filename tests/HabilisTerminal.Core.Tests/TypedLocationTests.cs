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
