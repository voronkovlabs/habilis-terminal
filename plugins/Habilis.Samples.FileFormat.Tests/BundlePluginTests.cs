using HabilisTerminal.Plugins;

namespace Habilis.Samples.FileFormat.Tests;

public sealed class BundlePluginTests : IDisposable
{
    private const string Sample = "= readme.txt\nhello\n= docs/guide.txt\nguide\n= docs/more/deep.txt\ndeep\n";

    private readonly string _folder = Directory.CreateTempSubdirectory("habi-tests-bundle-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private async Task<BundlePanel?> Open(string text, string inner = "", bool anyFormat = false)
    {
        var path = Path.Combine(_folder, "a.habibundle");
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        return (BundlePanel?)await new BundlePlugin().OpenFileAsync(new FileOpenRequest(path, inner, anyFormat), TestContext.Current.CancellationToken);
    }

    private static async Task<List<string>> Names(BundlePanel panel)
    {
        var names = new List<string>();
        await foreach (var batch in panel.ListAsync(TestContext.Current.CancellationToken))
        {
            names.AddRange(batch.Select(e => e.IsFolder ? e.Name + "/" : e.Name));
        }

        return names;
    }

    [Fact]
    public async Task ListsAndGoesInAndOut()
    {
        var token = TestContext.Current.CancellationToken;
        await using var panel = await Open(Sample);
        Assert.NotNull(panel);
        Assert.Equal(["docs/", "readme.txt"], await Names(panel));

        Assert.True(await panel.EnterAsync(new PanelEntry("docs", IsFolder: true), token));
        Assert.EndsWith(@"a.habibundle\docs", panel.Location, StringComparison.Ordinal);
        Assert.Equal(["more/", "guide.txt"], await Names(panel));

        Assert.True(await panel.GoUpAsync(token));
        Assert.False(await panel.GoUpAsync(token));
    }

    [Fact]
    public async Task AFileThatIsNoBundleIsNotOurs() => Assert.Null(await Open("just some text", anyFormat: true));

    [Theory]
    [InlineData("= ../../evil.txt\nx")]
    [InlineData("= C:/evil.txt\nx")]
    [InlineData("= a\\b.txt\nx")]
    [InlineData("= a/../../b.txt\nx")]
    [InlineData("= /rooted.txt\nx")]
    public async Task APathThatCouldEscapeMakesItNoBundle(string text) => Assert.Null(await Open(text));

    [Fact]
    public async Task AnAnswerForAllIsAskedOnceAndNothingPartialStays()
    {
        var token = TestContext.Current.CancellationToken;
        await using var panel = await Open(Sample);
        var target = Directory.CreateDirectory(Path.Combine(_folder, "out")).FullName;
        Directory.CreateDirectory(Path.Combine(target, "docs", "more"));
        await File.WriteAllTextAsync(Path.Combine(target, "docs", "guide.txt"), "old", token);
        await File.WriteAllTextAsync(Path.Combine(target, "docs", "more", "deep.txt"), "old", token);
        var asked = 0;
        var request = new TransferRequest([new PanelEntry("docs", IsFolder: true)], target, move: false, _ =>
        {
            asked++;
            return Task.FromResult(OverwriteChoice.SkipAll);
        });

        Assert.Equal(new OperationOutcome(0, 2, 0), await panel!.GetFilesAsync(request, new NoProgress(), token));
        Assert.Equal(1, asked);
        Assert.Empty(Directory.EnumerateFiles(target, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task APlaceFromHistoryOpensAndAGoneOneShowsTheTop()
    {
        await using var there = await Open(Sample, @"docs\more");
        Assert.Equal(["deep.txt"], await Names(there!));
        await using var gone = await Open(Sample, "gone");
        Assert.Equal(["docs/", "readme.txt"], await Names(gone!));
    }

    [Fact]
    public async Task ViewsAndCopiesOut()
    {
        var token = TestContext.Current.CancellationToken;
        await using var panel = await Open(Sample);
        var view = await panel!.ViewAsync(new PanelEntry("readme.txt"), token);
        Assert.Equal("hello\n", view?.Text);

        var target = Directory.CreateDirectory(Path.Combine(_folder, "out")).FullName;
        var request = new TransferRequest([new PanelEntry("docs", IsFolder: true)], target, move: false, _ => Task.FromResult(OverwriteChoice.Overwrite));
        var outcome = await panel.GetFilesAsync(request, new NoProgress(), token);
        Assert.Equal(new OperationOutcome(2, 0, 0), outcome);
        Assert.Equal("deep\n", await File.ReadAllTextAsync(Path.Combine(target, "docs", "more", "deep.txt"), token));
    }

    private sealed class NoProgress : IOperationProgress
    {
        public void SetTotals(long? items, long? bytes)
        {
        }

        public void Begin(string item)
        {
        }

        public void AddBytes(long bytes)
        {
        }

        public void CompleteItem()
        {
        }
    }
}
