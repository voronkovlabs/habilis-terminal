using Habilis.Aws.Tests;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.S3.Tests;

/// <summary>Issue #383: S3 may hold a file and a folder of the same name side by side.</summary>
public class SameNameTests
{
    private static FakeS3 Twins() => new FakeS3()
        .Add("b", "data", "the file")
        .Add("b", "data/a.txt", "a")
        .Add("b", "data/sub/b.txt", "b");

    [Fact]
    public async Task Both_show_and_an_address_without_a_slash_opens_the_folder()
    {
        var panel = await S3Test.OpenAsync(Twins(), "s3://b/");
        var entries = await Listing.AllAsync(panel);
        Assert.Contains(entries, e => e is { Name: "data", IsFolder: true });
        Assert.Contains(entries, e => e is { Name: "data", IsFolder: false });

        var folder = await S3Test.OpenAsync(Twins(), "s3://b/data");
        Assert.Equal("s3://b/data/", folder.Location);
        Assert.Null(folder.InitialCursor);
    }

    [Fact]
    public async Task An_object_address_with_no_folder_of_its_name_opens_its_folder_with_the_cursor_on_it()
    {
        var panel = await S3Test.OpenAsync(new FakeS3().Add("b", "docs/report.csv", "r"), "s3://b/docs/report.csv");
        Assert.Equal("s3://b/docs/", panel.Location);
        Assert.Equal("report.csv", panel.InitialCursor);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Downloading_both_asks_and_renames_or_skips_the_file(bool rename)
    {
        var host = new FakeHost();
        new S3Settings().Save(host.Settings);
        var panel = await S3Test.OpenAsync(host, Twins(), "s3://b/");
        var entries = (await Listing.AllAsync(panel)).Where(e => e.Name == "data").ToList();
        host.ChooseAnswers.Enqueue(rename ? "Save them as \u201Cname (file)\u201D" : "Skip these files");
        using var temp = new TempFolder();

        var outcome = await S3Test.DownloadAsync(panel, entries, temp.Path, new FakeProgress());

        Assert.Equal("a", File.ReadAllText(temp[Path.Combine("data", "a.txt")]));
        Assert.Equal("b", File.ReadAllText(temp[Path.Combine("data", "sub", "b.txt")]));
        if (rename)
        {
            Assert.Equal(3, outcome.Succeeded);
            Assert.Equal("the file", File.ReadAllText(temp["data (file)"]));
        }
        else
        {
            Assert.Equal(2, outcome.Succeeded);
            Assert.False(File.Exists(temp["data (file)"]));
        }
    }

    [Fact]
    public async Task Cancelling_the_question_downloads_nothing()
    {
        var host = new FakeHost();
        new S3Settings().Save(host.Settings);
        var panel = await S3Test.OpenAsync(host, Twins(), "s3://b/");
        var entries = (await Listing.AllAsync(panel)).Where(e => e.Name == "data").ToList();
        using var temp = new TempFolder();

        var outcome = await S3Test.DownloadAsync(panel, entries, temp.Path, new FakeProgress());

        Assert.Equal(0, outcome.Succeeded);
        Assert.Empty(temp.Files());
    }
}
