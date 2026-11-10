using HabilisTerminal.Core.Plugins;

namespace HabilisTerminal.Core.Tests;

public sealed class PluginLocationsTests
{
    [Theory]
    [InlineData("s3://bucket/x/y/", new[] { "y" })]
    [InlineData("gh:me/app/main", new[] { "main" })]
    [InlineData("gh:me", new[] { "me" })]
    [InlineData("gh:me/app/experiment%2Fconsole-frame", new[] { "experiment%2Fconsole-frame", "experiment/console-frame" })]
    [InlineData("gh:me/app/100%", new[] { "100%" })]
    [InlineData("gh:", new string[0])]
    [InlineData(@"C:\data\a.zip\docs", new[] { "docs" })]
    public void The_cursor_returns_to_the_place_left(string location, string[] names) =>
        Assert.Equal(names, PluginLocations.NamesAfterUp(location));
}
