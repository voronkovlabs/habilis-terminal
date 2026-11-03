using HabilisTerminal.Core.Settings;

namespace HabilisTerminal.Core.Tests;

public class ReleaseVersionsTests
{
    [Theory]
    [InlineData("v0.1.7", "0.1.6", "0.1.7")]
    [InlineData("v0.1.7", "0.1.6+abc123", "0.1.7")]
    [InlineData("v0.1.6", "0.1.6", null)]
    [InlineData("v0.1.6", "0.1.6.0", null)]
    [InlineData("v0.1.5", "0.1.6", null)]
    [InlineData("v0.2", "0.1.9", "0.2")]
    [InlineData("nightly", "0.1.6", null)]
    public void A_newer_release_is_found_only_when_its_tag_is_a_higher_version(string tag, string current, string? expected)
    {
        Assert.Equal(expected, ReleaseVersions.Newer(tag, current)?.ToString());
    }

    [Fact]
    public void The_tag_comes_from_githubs_latest_release_response()
    {
        Assert.Equal("v0.1.6", ReleaseVersions.TagOf("""{ "tag_name": "v0.1.6", "name": "Habilis Terminal 0.1.6" }"""));
        Assert.Null(ReleaseVersions.TagOf("""{ "message": "Not Found" }"""));
        Assert.Null(ReleaseVersions.TagOf("<html>"));
    }
}
