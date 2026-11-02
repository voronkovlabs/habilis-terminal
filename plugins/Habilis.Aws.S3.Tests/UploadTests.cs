using Habilis.Aws.Tests;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.S3.Tests;

public class UploadTests
{
    private const int MB = 1024 * 1024;

    [Fact]
    public async Task A_tree_uploads_into_the_current_folder_with_slash_separated_keys()
    {
        var api = new FakeS3().Add("b", "into/", string.Empty);
        var panel = await S3Test.OpenAsync(api, "s3://b/into/");
        using var temp = new TempFolder();
        temp.Write("tree\\a.txt", "a"u8.ToArray());
        temp.Write("tree\\sub\\b.bin", S3Test.Bytes(1000));
        temp.Write("tree\\sub\\deeper\\c.txt", "ccc"u8.ToArray());
        Directory.CreateDirectory(temp["tree\\empty"]);
        var single = temp.Write("single file.txt", "s"u8.ToArray());
        var progress = new FakeProgress();

        var outcome = await panel.PutFilesAsync([temp["tree"], single], S3Test.Answer(OverwriteChoice.Cancel), progress, CancellationToken.None);

        Assert.Equal(new OperationOutcome(5, 0, 0), outcome);
        Assert.Equal(
            ["into/", "into/single file.txt", "into/tree/a.txt", "into/tree/empty/", "into/tree/sub/b.bin", "into/tree/sub/deeper/c.txt"],
            api.Keys("b"));
        Assert.Equal(S3Test.Bytes(1000), api.Data("b", "into/tree/sub/b.bin"));
        Assert.Equal(((long?)5, (long?)1005), progress.Totals);
        Assert.Equal(1005, progress.Bytes);
        Assert.Equal(5, progress.Items);
    }

    [Fact]
    public async Task Large_files_upload_in_parallel_parts_that_join_exactly()
    {
        var data = S3Test.Bytes((11 * MB) + 7, seed: 3);
        var api = new FakeS3().AddBucket("b", "eu-central-1");
        var panel = await S3Test.OpenAsync(api, "s3://b/", new S3Settings { PartSizeMb = 5, MaxParallel = 3 });
        using var temp = new TempFolder();
        var path = temp.Write("big.bin", data);
        var progress = new FakeProgress();

        var outcome = await panel.PutFilesAsync([path], S3Test.Answer(OverwriteChoice.Cancel), progress, CancellationToken.None);

        Assert.Equal(1, outcome.Succeeded);
        Assert.Equal(data, api.Data("b", "big.bin"));
        Assert.Equal(data.Length, progress.Bytes);
        Assert.Equal(0, api.OpenUploads);
        Assert.Empty(api.Aborted);
    }

    [Fact]
    public async Task A_failed_part_aborts_the_multipart_upload()
    {
        var api = new FakeS3 { FailPart = 2 }.AddBucket("b");
        var host = new FakeHost();
        new S3Settings { PartSizeMb = 5 }.Save(host.Settings);
        var panel = await S3Test.OpenAsync(host, api, "s3://b/");
        using var temp = new TempFolder();
        var path = temp.Write("big.bin", S3Test.Bytes(11 * MB));

        var outcome = await panel.PutFilesAsync([path], S3Test.Answer(OverwriteChoice.Cancel), new FakeProgress(), CancellationToken.None);

        Assert.Equal((0, 0, 1), (outcome.Succeeded, outcome.Skipped, outcome.Failed));
        Assert.Single(api.Aborted);
        Assert.Equal(0, api.OpenUploads);
        Assert.Empty(api.Keys("b"));
        Assert.Contains("internal error", outcome.Message, StringComparison.Ordinal);
        Assert.Contains(host.Messages, m => m.Kind == MessageKind.Warning);
    }

    [Fact]
    public async Task Cancelling_aborts_the_multipart_upload()
    {
        var api = new FakeS3 { StallPart = 2 }.AddBucket("b");
        var panel = await S3Test.OpenAsync(api, "s3://b/", new S3Settings { PartSizeMb = 5 });
        using var temp = new TempFolder();
        var path = temp.Write("big.bin", S3Test.Bytes(11 * MB));
        using var cancel = new CancellationTokenSource();

        var upload = panel.PutFilesAsync([path], S3Test.Answer(OverwriteChoice.Cancel), new FakeProgress(), cancel.Token);
        await api.PartStalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        Assert.Single(api.Aborted);
        Assert.Equal(0, api.OpenUploads);
        Assert.Empty(api.Keys("b"));
    }

    [Theory]
    [InlineData(OverwriteChoice.Skip, "old", 0, 1)]
    [InlineData(OverwriteChoice.Overwrite, "new", 1, 0)]
    public async Task An_existing_object_is_replaced_only_when_the_user_says_so(OverwriteChoice choice, string expected, int succeeded, int skipped)
    {
        var api = new FakeS3().Add("b", "f.txt", "old");
        var panel = await S3Test.OpenAsync(api, "s3://b/");
        using var temp = new TempFolder();
        var path = temp.Write("f.txt", "new"u8.ToArray());
        var asked = new List<string>();

        var outcome = await panel.PutFilesAsync([path], S3Test.Answer(choice, asked), new FakeProgress(), CancellationToken.None);

        Assert.Equal(["f.txt"], asked);
        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(api.Data("b", "f.txt")!));
        Assert.Equal((succeeded, skipped), (outcome.Succeeded, outcome.Skipped));
    }

    [Fact]
    public async Task Uploading_into_the_bucket_list_is_refused()
    {
        var panel = await S3Test.OpenAsync(new FakeS3().AddBucket("b"));
        using var temp = new TempFolder();
        var path = temp.Write("f.txt", "x"u8.ToArray());

        var error = await Assert.ThrowsAsync<PluginException>(() => panel.PutFilesAsync([path], S3Test.Answer(OverwriteChoice.Cancel), new FakeProgress(), CancellationToken.None));

        Assert.Contains("open a bucket", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_local_file_counts_as_failed_so_a_move_keeps_the_originals()
    {
        var api = new FakeS3().AddBucket("b");
        var panel = await S3Test.OpenAsync(api, "s3://b/");
        using var temp = new TempFolder();
        var path = temp.Write("ok.txt", "x"u8.ToArray());

        var outcome = await panel.PutFilesAsync([path, temp["gone.txt"]], S3Test.Answer(OverwriteChoice.Cancel), new FakeProgress(), CancellationToken.None);

        Assert.Equal((1, 1), (outcome.Succeeded, outcome.Failed));
        Assert.Equal(["ok.txt"], api.Keys("b"));
    }
}
