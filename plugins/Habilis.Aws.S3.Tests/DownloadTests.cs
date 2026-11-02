using System.Net;
using Amazon.S3;
using Habilis.Aws.Tests;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.S3.Tests;

public class DownloadTests
{
    private const int MB = 1024 * 1024;

    [Fact]
    public async Task A_file_downloads_byte_identical_with_progress()
    {
        var data = S3Test.Bytes(300_000);
        var api = new FakeS3().Add("b", "docs/report.bin", data);
        var panel = await S3Test.OpenAsync(api, "s3://b/docs/");
        using var temp = new TempFolder();
        var progress = new FakeProgress();

        var outcome = await S3Test.DownloadAsync(panel, [await S3Test.EntryAsync(panel, "report.bin")], temp.Path, progress);

        Assert.Equal(new OperationOutcome(1, 0, 0), outcome);
        Assert.Equal(data, File.ReadAllBytes(temp["report.bin"]));
        Assert.Equal(((long?)1, (long?)data.Length), progress.Totals);
        Assert.Equal(data.Length, progress.Bytes);
        Assert.Equal(1, progress.Items);
        Assert.Equal(["docs/report.bin"], progress.Begun);
        Assert.Equal(["report.bin"], temp.Files());
    }

    [Fact]
    public async Task A_folder_downloads_with_its_tree_mapped_to_safe_names()
    {
        var api = new FakeS3()
            .Add("b", "data/", string.Empty)
            .Add("b", "data/a.txt", "a")
            .Add("b", "data/sub/what?.txt", "q")
            .Add("b", "data/sub//odd.txt", "o")
            .Add("b", "data/empty/", string.Empty)
            .Add("b", "data/zero.bin", string.Empty)
            .Add("b", "other.txt", "not this");
        var panel = await S3Test.OpenAsync(api, "s3://b/");
        using var temp = new TempFolder();
        var progress = new FakeProgress();

        var outcome = await S3Test.DownloadAsync(panel, [await S3Test.EntryAsync(panel, "data")], temp.Path, progress);

        Assert.Equal(new OperationOutcome(4, 0, 0), outcome);
        Assert.Equal(["data\\a.txt", "data\\sub\\%2F\\odd.txt", "data\\sub\\what%3F.txt", "data\\zero.bin"], temp.Files());
        Assert.Equal("q", File.ReadAllText(temp["data\\sub\\what%3F.txt"]));
        Assert.Empty(File.ReadAllBytes(temp["data\\zero.bin"]));
        Assert.True(Directory.Exists(temp["data\\empty"]));
        Assert.Equal(((long?)4, (long?)3), progress.Totals);
    }

    [Fact]
    public async Task A_whole_bucket_downloads_from_the_bucket_list()
    {
        var api = new FakeS3().AddBucket("eu-bucket", "EU", listRegion: true).Add("eu-bucket", "x/1.txt", "1").Add("eu-bucket", "2.txt", "2");
        var panel = await S3Test.OpenAsync(api);
        using var temp = new TempFolder();

        var outcome = await S3Test.DownloadAsync(panel, [await S3Test.EntryAsync(panel, "eu-bucket")], temp.Path, new FakeProgress());

        Assert.Equal(2, outcome.Succeeded);
        Assert.Equal(["eu-bucket\\2.txt", "eu-bucket\\x\\1.txt"], temp.Files());
    }

    [Fact]
    public async Task Large_files_download_in_parallel_ranged_parts_that_reassemble_exactly()
    {
        var data = S3Test.Bytes((12 * MB) + 12345, seed: 7);
        var api = new FakeS3().Add("b", "big.bin", data);
        var panel = await S3Test.OpenAsync(api, "s3://b/", new S3Settings { PartSizeMb = 5, MaxParallel = 4 });
        using var temp = new TempFolder();
        var progress = new FakeProgress();

        var outcome = await S3Test.DownloadAsync(panel, [await S3Test.EntryAsync(panel, "big.bin")], temp.Path, progress);

        Assert.Equal(1, outcome.Succeeded);
        Assert.Equal(data, File.ReadAllBytes(temp["big.bin"]));
        Assert.Equal([(0L, 5L * MB), (5L * MB, 5L * MB), (10L * MB, (2L * MB) + 12345)], api.Reads.Select(r => (r.Offset, r.Length)).Order());
        Assert.Equal(data.Length, progress.Bytes);
    }

    [Theory]
    [InlineData(OverwriteChoice.Skip, "old", 0, 1)]
    [InlineData(OverwriteChoice.SkipAll, "old", 0, 1)]
    [InlineData(OverwriteChoice.Overwrite, "new", 1, 0)]
    [InlineData(OverwriteChoice.OverwriteAll, "new", 1, 0)]
    public async Task An_existing_local_file_is_replaced_only_when_the_user_says_so(OverwriteChoice choice, string expected, int succeeded, int skipped)
    {
        var api = new FakeS3().Add("b", "f.txt", "new");
        var panel = await S3Test.OpenAsync(api, "s3://b/");
        using var temp = new TempFolder();
        temp.Write("f.txt", "old"u8.ToArray());
        var asked = new List<string>();

        var outcome = await S3Test.DownloadAsync(panel, [await S3Test.EntryAsync(panel, "f.txt")], temp.Path, new FakeProgress(), S3Test.Answer(choice, asked));

        Assert.Equal(["f.txt"], asked);
        Assert.Equal(expected, File.ReadAllText(temp["f.txt"]));
        Assert.Equal((succeeded, skipped, 0), (outcome.Succeeded, outcome.Skipped, outcome.Failed));
        Assert.Equal(["f.txt"], temp.Files());
    }

    [Fact]
    public async Task All_answers_are_asked_once_and_cancel_stops_the_rest()
    {
        var api = new FakeS3().Add("b", "d/1.txt", "n").Add("b", "d/2.txt", "n").Add("b", "d/3.txt", "n");
        var panel = await S3Test.OpenAsync(api, "s3://b/", new S3Settings { MaxParallel = 1 });
        using var temp = new TempFolder();
        foreach (var name in new[] { "1.txt", "2.txt", "3.txt" })
        {
            temp.Write($"d\\{name}", "o"u8.ToArray());
        }

        var asked = new List<string>();
        var all = await S3Test.DownloadAsync(panel, [await S3Test.EntryAsync(panel, "d")], temp.Path, new FakeProgress(), S3Test.Answer(OverwriteChoice.OverwriteAll, asked));
        Assert.Single(asked);
        Assert.Equal(3, all.Succeeded);

        foreach (var name in new[] { "1.txt", "2.txt", "3.txt" })
        {
            temp.Write($"d\\{name}", "o"u8.ToArray());
        }

        var cancelled = await S3Test.DownloadAsync(panel, [await S3Test.EntryAsync(panel, "d")], temp.Path, new FakeProgress(), S3Test.Answer(OverwriteChoice.Cancel));
        Assert.Equal((0, 3, 0), (cancelled.Succeeded, cancelled.Skipped, cancelled.Failed));
        Assert.StartsWith("Cancelled", cancelled.Message, StringComparison.Ordinal);
        Assert.All(temp.Files(), f => Assert.Equal("o", File.ReadAllText(temp[f])));
    }

    [Fact]
    public async Task Cancelling_leaves_no_partial_files()
    {
        var api = new FakeS3().Add("b", "slow.bin", S3Test.Bytes(50_000)).Add("b", "fast.bin", S3Test.Bytes(10));
        api.StallReadsOf = "slow.bin";
        var panel = await S3Test.OpenAsync(api, "s3://b/");
        var entries = await Listing.AllAsync(panel);
        using var temp = new TempFolder();
        using var cancel = new CancellationTokenSource();
        var progress = new FakeProgress();

        var download = S3Test.DownloadAsync(panel, entries, temp.Path, progress, cancellationToken: cancel.Token);
        await api.ReadStalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(temp["slow.bin" + S3Panel.PartialSuffix]));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.DoesNotContain(temp.Files(), f => f.StartsWith("slow.bin", StringComparison.Ordinal));
        Assert.DoesNotContain(temp.Files(), f => f.EndsWith(S3Panel.PartialSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_part_leaves_no_partial_file_and_is_reported()
    {
        var api = new FakeS3().Add("b", "bad.bin", S3Test.Bytes(100)).Add("b", "good.bin", S3Test.Bytes(100));
        api.ReadErrors["bad.bin"] = new AmazonS3Exception("We encountered an internal error.") { ErrorCode = "InternalError", StatusCode = HttpStatusCode.InternalServerError };
        var host = new FakeHost();
        new S3Settings().Save(host.Settings);
        var panel = await S3Test.OpenAsync(host, api, "s3://b/");
        using var temp = new TempFolder();

        var outcome = await S3Test.DownloadAsync(panel, await Listing.AllAsync(panel), temp.Path, new FakeProgress());

        Assert.Equal((1, 0, 1), (outcome.Succeeded, outcome.Skipped, outcome.Failed));
        Assert.Equal(["good.bin"], temp.Files());
        Assert.Contains("Downloading bad.bin failed", outcome.Message, StringComparison.Ordinal);
        Assert.Contains(host.Messages, m => m.Kind == MessageKind.Warning && m.Message.Contains("bad.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_archived_object_says_it_must_be_restored()
    {
        var api = new FakeS3().Add("b", "cold.bin", S3Test.Bytes(10), "GLACIER");
        api.ReadErrors["cold.bin"] = new AmazonS3Exception("The operation is not valid for the object's storage class") { ErrorCode = "InvalidObjectState", StatusCode = HttpStatusCode.Forbidden };
        var panel = await S3Test.OpenAsync(api, "s3://b/");
        using var temp = new TempFolder();

        var outcome = await S3Test.DownloadAsync(panel, await Listing.AllAsync(panel), temp.Path, new FakeProgress());

        Assert.Equal(1, outcome.Failed);
        Assert.Contains("archived", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("restored", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(temp.Files());
    }

    [Fact]
    public async Task An_object_changed_since_listing_is_not_downloaded_stale()
    {
        var api = new FakeS3().Add("b", "f.txt", "v1");
        var panel = await S3Test.OpenAsync(api, "s3://b/");
        var entry = await S3Test.EntryAsync(panel, "f.txt");
        api.Add("b", "f.txt", "v2, longer");
        using var temp = new TempFolder();

        var outcome = await S3Test.DownloadAsync(panel, [entry], temp.Path, new FakeProgress());

        Assert.Equal(1, outcome.Failed);
        Assert.Contains("changed in S3 after it was listed", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(temp.Files());
    }

    [Fact]
    public async Task Keys_differing_only_in_case_are_not_mixed_up_on_disk()
    {
        var api = new FakeS3().Add("b", "d/Read.me", "upper").Add("b", "d/read.me", "lower");
        var panel = await S3Test.OpenAsync(api, "s3://b/");
        using var temp = new TempFolder();

        var outcome = await S3Test.DownloadAsync(panel, [await S3Test.EntryAsync(panel, "d")], temp.Path, new FakeProgress());

        Assert.Equal((1, 1), (outcome.Succeeded, outcome.Failed));
        Assert.Equal("upper", File.ReadAllText(temp["d\\Read.me"]));
        Assert.Contains("letter case", outcome.Message, StringComparison.Ordinal);
    }
}
