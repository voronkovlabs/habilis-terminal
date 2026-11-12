using HabilisTerminal.Core.FileSystem;

namespace HabilisTerminal.Core.Tests;

public class LongPathTests
{
    [WindowsTheory]
    [InlineData(@"C:\a\b", @"\\?\C:\a\b")]
    [InlineData(@"\\server\share\dir", @"\\?\UNC\server\share\dir")]
    [InlineData(@"\\?\C:\already", @"\\?\C:\already")]
    [InlineData(@"\\?\UNC\server\share", @"\\?\UNC\server\share")]
    public void Converts_to_extended_form(string path, string expected)
    {
        Assert.Equal(expected, LongPath.ToExtended(path));
    }

    [Theory]
    [InlineData(@"\\?\C:\a", @"C:\a")]
    [InlineData(@"\\?\UNC\server\share\x", @"\\server\share\x")]
    [InlineData(@"C:\plain", @"C:\plain")]
    public void Converts_back_for_display(string path, string expected)
    {
        Assert.Equal(expected, LongPath.ToDisplay(path));
    }

    [WindowsTheory]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\?\UNC\server\share", true)]
    [InlineData(@"\\?\C:\a", false)]
    [InlineData(@"C:\a", false)]
    public void Detects_unc(string path, bool expected)
    {
        Assert.Equal(expected, LongPath.IsUnc(path));
    }
}
