using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Habilis.Aws.Common;
using Habilis.Aws.Tests;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.S3.Tests;

public class OperationTests
{
    [Fact]
    public async Task Delete_removes_objects_and_whole_folders_in_batches_and_reports_keys_that_failed()
    {
        var api = new FakeS3 { PageSize = 1000 }.Add("b", "keep.txt", "k").Add("b", "f.txt", "f").Add("b", "dir/", string.Empty);
        for (var i = 0; i < 1500; i++)
        {
            api.Add("b", $"dir/sub/{i:D4}.txt", "x");
        }

        api.UndeletableKeys.Add("dir/sub/0007.txt");
        var host = new FakeHost();
        var panel = await S3Test.OpenAsync(host, api, "s3://b/");
        var entries = (await Listing.AllAsync(panel)).Where(e => e.Name is "f.txt" or "dir").ToList();
        var progress = new FakeProgress();

        var outcome = await panel.DeleteAsync(entries, progress, CancellationToken.None);

        Assert.Equal((1501, 1), (outcome.Succeeded, outcome.Failed));
        Assert.Equal(["dir/sub/0007.txt", "keep.txt"], api.Keys("b"));
        Assert.Contains("dir/sub/0007.txt", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("s3:DeleteObject", outcome.Message, StringComparison.Ordinal);
        Assert.All(api.DeleteBatchSizes, size => Assert.InRange(size, 1, 1000));
        Assert.Equal(1501, progress.Items);
        Assert.Contains(host.Messages, m => m.Title == "Delete" && m.Kind == MessageKind.Warning);
    }

    [Fact]
    public async Task Deleting_buckets_is_refused()
    {
        var api = new FakeS3().Add("b", "x", "x");
        var panel = await S3Test.OpenAsync(api);

        var error = await Assert.ThrowsAsync<PluginException>(async () =>
            await panel.DeleteAsync(await Listing.AllAsync(panel), new FakeProgress(), CancellationToken.None));

        Assert.Contains("Deleting buckets is not supported", error.Message, StringComparison.Ordinal);
        Assert.Equal(["x"], api.Keys("b"));
    }

    [Fact]
    public async Task Make_folder_creates_a_folder_marker_that_lists_as_a_folder()
    {
        var api = new FakeS3().Add("b", "a/x.txt", "x");
        var panel = await S3Test.OpenAsync(api, "s3://b/a/");

        await panel.MakeFolderAsync("new folder?", CancellationToken.None);

        Assert.Contains("a/new folder?/", api.Keys("b"));
        Assert.True((await S3Test.EntryAsync(panel, "new folder?")).IsFolder);
    }

    [Theory]
    [InlineData("a/b", "cannot contain \"/\"")]
    [InlineData("", "Type a name")]
    [InlineData("..", "cannot be used")]
    public async Task Make_folder_refuses_bad_names(string name, string message)
    {
        var api = new FakeS3().AddBucket("b");
        var panel = await S3Test.OpenAsync(api, "s3://b/");

        var error = await Assert.ThrowsAsync<PluginException>(() => panel.MakeFolderAsync(name, CancellationToken.None));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.Empty(api.Keys("b"));
    }

    [Fact]
    public async Task Make_folder_in_the_bucket_list_is_refused()
    {
        var panel = await S3Test.OpenAsync(new FakeS3());

        var error = await Assert.ThrowsAsync<PluginException>(() => panel.MakeFolderAsync("new", CancellationToken.None));

        Assert.Contains("Creating buckets is not supported", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Access_denied_on_listing_names_the_permission()
    {
        var api = new FakeS3 { ListError = NavigationTests.Denied() }.AddBucket("b");
        var panel = await S3Test.OpenAsync(api, "s3://b/");

        var error = await Assert.ThrowsAsync<PluginException>(() => Listing.AllAsync(panel));

        Assert.Equal("Listing s3://b/ failed. Permission missing: s3:ListBucket. Ask for this permission on the profile's role or user.", error.Message);
    }

    [Fact]
    public async Task A_missing_bucket_says_so()
    {
        var panel = await S3Test.OpenAsync(new FakeS3(), "s3://nope/");

        var error = await Assert.ThrowsAsync<PluginException>(() => Listing.AllAsync(panel));

        Assert.Contains("bucket does not exist", error.Message, StringComparison.Ordinal);
        Assert.Contains("nope", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Errors_read_as_plain_language()
    {
        var session = new AwsSession("prod", "eu-west-1");

        Assert.Equal(
            "Listing failed. Permission missing: s3:ListBucket. Ask for this permission on the profile's role or user.",
            S3Errors.Describe(new AmazonS3Exception("User: arn:aws:iam::1:user/x is not authorized to perform: s3:ListBucket on resource") { ErrorCode = "AccessDenied", StatusCode = HttpStatusCode.Forbidden }, "Listing", session, "s3:Other").Message);
        Assert.Contains("s3:GetObject", S3Errors.Describe(new AmazonS3Exception("Forbidden") { StatusCode = HttpStatusCode.Forbidden }, "Reading", session, "s3:GetObject").Message, StringComparison.Ordinal);
        Assert.Contains("have expired", S3Errors.Describe(new AmazonS3Exception("expired") { ErrorCode = "ExpiredToken", StatusCode = HttpStatusCode.BadRequest }, "Listing", session, "s3:ListBucket").Message, StringComparison.Ordinal);
        Assert.Contains("rejected the credentials", S3Errors.Describe(new AmazonS3Exception("bad key") { ErrorCode = "InvalidAccessKeyId", StatusCode = HttpStatusCode.Forbidden }, "Listing", session, "s3:ListBucket").Message, StringComparison.Ordinal);
        Assert.Contains("throttling", S3Errors.Describe(new AmazonS3Exception("slow down") { ErrorCode = "SlowDown", StatusCode = HttpStatusCode.ServiceUnavailable }, "Uploading", session, "s3:PutObject").Message, StringComparison.Ordinal);
        Assert.Contains("Cannot reach AWS", S3Errors.Describe(new HttpRequestException("no route"), "Listing", session, "s3:ListBucket").Message, StringComparison.Ordinal);
        Assert.Contains("did not answer within", S3Errors.Describe(new TaskCanceledException(), "Listing", session, "s3:ListBucket").Message, StringComparison.Ordinal);
        Assert.Contains("could not be used", S3Errors.Describe(new AmazonClientException("Unable to get credentials"), "Listing", session, "s3:ListBucket").Message, StringComparison.Ordinal);
    }
}
