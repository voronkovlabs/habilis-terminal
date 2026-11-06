using HabilisTerminal.Core.Plugins;

namespace HabilisTerminal.Core.Tests;

public class PluginFilesTests
{
    [Theory]
    [InlineData("a.zip", true)]
    [InlineData("A.ZIP", true)]
    [InlineData("logs.tar.gz", true)]
    [InlineData("zip", false)]
    [InlineData(".zip", false)]
    [InlineData("azip", false)]
    [InlineData("a.zip.txt", false)]
    public void A_name_is_claimed_by_its_ending(string name, bool claimed) =>
        Assert.Equal(claimed, PluginFiles.Claims(["zip", "tar.gz", ""], name));

    [Theory]
    [InlineData(@"C:\data\a.zip", @"C:\data\a.zip", "")]
    [InlineData(@"C:\data\a.zip\", @"C:\data\a.zip", "")]
    [InlineData(@"C:\data\a.zip\docs", @"C:\data\a.zip", "docs")]
    [InlineData(@"C:\data\a.zip\docs\sub\", @"C:\data\a.zip", @"docs\sub")]
    public void A_location_splits_at_the_file(string location, string file, string inner)
    {
        var split = PluginFiles.SplitLocation(location, path => path == @"C:\data\a.zip");
        Assert.Equal((file, inner), split);
    }

    [Theory]
    [InlineData(@"C:\data\gone\x")]
    [InlineData(@"C:\")]
    [InlineData("")]
    public void A_location_with_no_file_does_not_split(string location) =>
        Assert.Null(PluginFiles.SplitLocation(location, _ => false));
}
