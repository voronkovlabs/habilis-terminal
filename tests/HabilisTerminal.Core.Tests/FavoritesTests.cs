using HabilisTerminal.Core.Panels;

namespace HabilisTerminal.Core.Tests;

/// <summary>Issue #385: favorite folders for the Change Drive menu.</summary>
public class FavoritesTests
{
    [Fact]
    public void Toggle_adds_and_removes_and_keeps_the_order()
    {
        var favorites = new Favorites();
        Assert.True(favorites.Toggle(@"C:\Projects\app"));
        Assert.True(favorites.Toggle("s3://bucket/data/"));
        Assert.True(favorites.Contains(@"c:\projects\APP\"));
        Assert.Equal([@"C:\Projects\app", "s3://bucket/data/"], favorites.Items);

        Assert.False(favorites.Toggle(@"C:\Projects\app\"));
        Assert.Equal(["s3://bucket/data/"], favorites.Items);
    }

    [Fact]
    public void Plugin_addresses_keep_their_case_and_duplicates_load_once()
    {
        var favorites = new Favorites(["s3://bucket/Data/", "s3://bucket/Data", "", @"D:\x"]);
        Assert.Equal(["s3://bucket/Data/", @"D:\x"], favorites.Items);
        Assert.False(favorites.Contains("s3://bucket/data/"));
    }

    [Theory]
    [InlineData(@"C:\Projects\app", "app")]
    [InlineData(@"C:\Projects\app\", "app")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("s3://bucket/data/", "data")]
    [InlineData(@"\\server\share", "share")]
    public void The_title_is_the_last_part(string location, string title) =>
        Assert.Equal(title, Favorites.Title(location));
}
