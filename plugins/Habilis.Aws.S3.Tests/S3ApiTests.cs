using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Amazon.Runtime;
using Habilis.Aws.Common;

namespace Habilis.Aws.S3.Tests;

/// <summary>
/// The real <see cref="S3Api"/> over the AWS SDK, against canned HTTP answers instead of the network: how requests
/// are built and answers read.
/// </summary>
public class S3ApiTests
{
    private const string Xmlns = "xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"";

    [Fact]
    public async Task Listings_ask_for_url_encoding_and_decode_keys_and_prefixes()
    {
        var http = new CannedHttp();
        http.Answers.Enqueue(Ok($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ListBucketResult {Xmlns}><Name>b</Name><Prefix>a%2F</Prefix><KeyCount>2</KeyCount><MaxKeys>1000</MaxKeys><Delimiter>%2F</Delimiter><EncodingType>url</EncodingType><IsTruncated>true</IsTruncated><NextContinuationToken>next1</NextContinuationToken>
            <Contents><Key>a%2Fq%3F%01x%2541+%2B.txt</Key><LastModified>2024-01-01T00:00:00.000Z</LastModified><ETag>"e1"</ETag><Size>5</Size><StorageClass>GLACIER</StorageClass></Contents>
            <CommonPrefixes><Prefix>a%2F%2F</Prefix></CommonPrefixes>
            </ListBucketResult>
            """));
        http.Answers.Enqueue(Ok($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ListBucketResult {Xmlns}><Name>b</Name><KeyCount>0</KeyCount><IsTruncated>false</IsTruncated></ListBucketResult>
            """));
        using var api = Api(http);

        var pages = new List<S3Page>();
        await foreach (var page in api.ListObjectsAsync("eu-west-1", "b", "a/", recursive: false, CancellationToken.None))
        {
            pages.Add(page);
        }

        Assert.Equal(2, pages.Count);
        var info = Assert.Single(pages[0].Objects);
        Assert.Equal("a/q?\u0001x%41 +.txt", info.Key);
        Assert.Equal(5, info.Size);
        Assert.Equal("GLACIER", info.StorageClass);
        Assert.Equal(DateTimeKind.Utc, info.Modified!.Value.Kind);
        Assert.Equal(["a//"], pages[0].Prefixes);
        var first = http.Requests.First().Uri;
        Assert.Contains("encoding-type=url", first, StringComparison.Ordinal);
        Assert.Contains("delimiter=%2F", first, StringComparison.Ordinal);
        Assert.Contains("prefix=a%2F", first, StringComparison.Ordinal);
        Assert.StartsWith("https://s3.eu-west-1.amazonaws.com/", first, StringComparison.Ordinal);
        Assert.Contains("continuation-token=next1", http.Requests.Last().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keys_starting_with_a_slash_reach_s3_exactly()
    {
        var http = new CannedHttp();
        http.Answers.Enqueue(Ok(string.Empty));
        using var api = Api(http);

        await api.HeadObjectAsync("us-east-1", "b", "/lead", CancellationToken.None);

        Assert.EndsWith("/b//lead", http.Requests.Single().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_object_heads_as_null()
    {
        var http = new CannedHttp();
        http.Answers.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        using var api = Api(http);

        Assert.Null(await api.HeadObjectAsync("us-east-1", "b", "k", CancellationToken.None));
    }

    [Fact]
    public async Task Ranged_reads_send_the_range_and_etag()
    {
        var http = new CannedHttp();
        http.Answers.Enqueue(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1, 2, 3]) });
        using var api = Api(http);

        await using var stream = await api.OpenReadAsync("us-east-1", "b", "k", 10, 3, "\"e1\"", CancellationToken.None);
        var buffer = new byte[3];
        await stream.ReadExactlyAsync(buffer);

        Assert.Equal([1, 2, 3], buffer);
        var request = http.Requests.Single();
        Assert.Equal("bytes=10-12", request.Range);
        Assert.Equal("\"e1\"", request.IfMatch);
    }

    [Fact]
    public async Task DeleteObjects_returns_the_keys_that_failed()
    {
        var http = new CannedHttp();
        http.Answers.Enqueue(Ok($"""<?xml version="1.0" encoding="UTF-8"?><DeleteResult {Xmlns}><Error><Key>/y</Key><Code>AccessDenied</Code><Message>Access Denied</Message></Error></DeleteResult>"""));
        using var api = Api(http);

        var failures = await api.DeleteObjectsAsync("us-east-1", "b", ["x", "/y"], CancellationToken.None);

        Assert.Equal([new S3DeleteFailure("/y", "AccessDenied", "Access Denied")], failures);
        Assert.Contains("<Key>/y</Key>", http.Requests.Single().Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bucket_location_and_bucket_list_regions_are_read()
    {
        var http = new CannedHttp();
        http.Answers.Enqueue(Ok($"""<?xml version="1.0" encoding="UTF-8"?><LocationConstraint {Xmlns}/>"""));
        http.Answers.Enqueue(Ok($"""<?xml version="1.0" encoding="UTF-8"?><LocationConstraint {Xmlns}>EU</LocationConstraint>"""));
        http.Answers.Enqueue(Ok($"""<?xml version="1.0" encoding="UTF-8"?><ListAllMyBucketsResult {Xmlns}><Buckets><Bucket><Name>b1</Name><CreationDate>2020-01-01T00:00:00.000Z</CreationDate><BucketRegion>ap-east-1</BucketRegion></Bucket></Buckets></ListAllMyBucketsResult>"""));
        using var api = Api(http);

        Assert.Equal("us-east-1", BucketRegions.FromLocation(await api.GetBucketLocationAsync("b", CancellationToken.None)));
        Assert.Equal("eu-west-1", BucketRegions.FromLocation(await api.GetBucketLocationAsync("b", CancellationToken.None)));
        var buckets = new List<S3BucketInfo>();
        await foreach (var page in api.ListBucketsAsync(CancellationToken.None))
        {
            buckets.AddRange(page);
        }

        Assert.Equal([new S3BucketInfo("b1", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), "ap-east-1")], buckets);
        Assert.Contains("max-buckets=1000", http.Requests.Last().Uri, StringComparison.Ordinal);
    }

    private static S3Api Api(CannedHttp http) =>
        new(new AwsSession("test", "us-east-1"), new AnonymousAWSCredentials(), new CannedFactory(http));

    private static HttpResponseMessage Ok(string xml) => new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

    private sealed record Sent(string Uri, string? Range, string? IfMatch, string Body);

    private sealed class CannedHttp : HttpMessageHandler
    {
        public ConcurrentQueue<HttpResponseMessage> Answers { get; } = new();

        public ConcurrentQueue<Sent> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(new Sent(request.RequestUri!.AbsoluteUri, request.Headers.Range?.ToString(), request.Headers.IfMatch.FirstOrDefault()?.ToString(), body));
            Assert.True(Answers.TryDequeue(out var response), "No canned answer left.");
            response.RequestMessage = request;
            response.Headers.Add("x-amz-request-id", "test");
            return response;
        }
    }

    private sealed class CannedFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler, disposeHandler: false);
    }
}
