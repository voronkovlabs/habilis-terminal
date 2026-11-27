using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Settings;

namespace HabilisTerminal.Core.Tests;

/// <summary>Issue #413: mask groups such as &lt;music&gt; wherever a mask is taken, from the settings.</summary>
[Collection(nameof(FileMaskGroupTests))]
public class FileMaskGroupTests : IDisposable
{
    public void Dispose()
    {
        FileMaskGroups.Reset();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("<pictures>", "photo.JPG", true)]
    [InlineData("<pictures>", "scan.heic", true)]
    [InlineData("<pictures>", "song.mp3", false)]
    [InlineData("<music>", "01 - Keep Yourself Alive.mp3", true)]
    [InlineData("<music>", "album.flac", true)]
    [InlineData("<music>", "cover.jpg", false)]
    [InlineData("<video>", "clip.mkv", true)]
    [InlineData("<documents>", "report.pdf", true)]
    [InlineData("<archives>", "backup.7z", true)]
    [InlineData("<code>", "Program.cs", true)]
    [InlineData("<MUSIC>", "a.ogg", true)]
    public void A_group_matches_its_types(string mask, string name, bool matches) =>
        Assert.Equal(matches, FileMask.Parse(mask).IsMatch(name));

    [Fact]
    public void Groups_combine_with_masks_and_exclusions()
    {
        var music = FileMask.Parse("<music>|*.wav");
        Assert.True(music.IsMatch("a.mp3"));
        Assert.False(music.IsMatch("a.wav"));

        var both = FileMask.Parse("<pictures>;*.kra");
        Assert.True(both.IsMatch("x.png"));
        Assert.True(both.IsMatch("x.kra"));
        Assert.False(both.IsMatch("x.txt"));

        var everythingButPictures = FileMask.Parse("|<pictures>");
        Assert.True(everythingButPictures.IsMatch("a.txt"));
        Assert.False(everythingButPictures.IsMatch("a.png"));
    }

    [Fact]
    public void An_unknown_group_names_the_known_ones()
    {
        var error = Assert.Throws<FormatException>(() => FileMask.Parse("<songs>"));
        Assert.Contains("<songs>", error.Message);
        Assert.Contains("<music>", error.Message);
        Assert.False(FileMask.TryParse("<songs>", out _));
    }

    [Fact]
    public void Settings_change_a_group_and_empty_keeps_the_default()
    {
        FileMaskGroups.Apply(name => name switch { "music" => "*.xm;*.mod", "video" => "  ", _ => null });
        Assert.True(FileMask.Parse("<music>").IsMatch("tune.xm"));
        Assert.False(FileMask.Parse("<music>").IsMatch("tune.mp3"));
        Assert.True(FileMask.Parse("<video>").IsMatch("a.mp4"));
    }

    [Fact]
    public void Every_group_has_a_setting_with_its_default()
    {
        foreach (var group in FileMaskGroups.Defaults)
        {
            var setting = Assert.Single(SettingsCatalog.All, s => s.Key == $"masks.{group.Name}");
            Assert.Equal(group.Masks, setting.Default);
            Assert.Equal(SettingsCatalog.MaskGroups, setting.Category);
        }
    }

    [Theory]
    [InlineData("*", "<music>")]
    [InlineData("*.*", "<music>")]
    [InlineData("", "<music>")]
    [InlineData("<pictures>", "<music>")]
    [InlineData("*.txt", "*.txt;<music>")]
    [InlineData("*.txt|*.bak", "*.txt;<music>|*.bak")]
    [InlineData("*.txt;<music>", "*.txt;<music>")]
    public void The_picker_puts_a_group_into_the_field(string field, string expected)
    {
        var music = FileMaskGroups.All.Single(g => g.Name == "music");
        Assert.Equal(expected, FileMaskGroups.AddTo(field, music));
    }
}
