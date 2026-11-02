using System.Net;
using System.Runtime.CompilerServices;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Habilis.Aws.Common;

namespace Habilis.Aws.S3;

/// <summary>A bucket of the bucket list; the region when S3 reports it with the list.</summary>
internal sealed record S3BucketInfo(string Name, DateTime? Created, string? Region = null);

/// <summary>An object: its exact key, size, last change (UTC), ETag and storage class.</summary>
internal sealed record S3ObjectInfo(string Key, long Size, DateTime? Modified = null, string? ETag = null, string? StorageClass = null);

/// <summary>One page of a listing: common prefixes (with their trailing "/") and objects.</summary>
internal sealed record S3Page(IReadOnlyList<string> Prefixes, IReadOnlyList<S3ObjectInfo> Objects);

/// <summary>A key that DeleteObjects could not delete, with S3's error code and message.</summary>
internal sealed record S3DeleteFailure(string Key, string Code, string Message);

/// <summary>The S3 calls the plugin makes; a fake stands in for it in the tests. Every call names the bucket's region.</summary>
internal interface IS3Api : IDisposable
{
    IAsyncEnumerable<IReadOnlyList<S3BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken);

    /// <summary>The bucket's location constraint as S3 reports it ("" for us-east-1, "EU" for eu-west-1).</summary>
    Task<string> GetBucketLocationAsync(string bucket, CancellationToken cancellationToken);

    /// <summary>
    /// Keys under <paramref name="prefix"/>, a page (up to 1000 keys) at a time: the next level with common prefixes,
    /// or every key below the prefix when <paramref name="recursive"/>.
    /// </summary>
    IAsyncEnumerable<S3Page> ListObjectsAsync(string region, string bucket, string prefix, bool recursive, CancellationToken cancellationToken);

    /// <summary>The object, or null when there is none.</summary>
    Task<S3ObjectInfo?> HeadObjectAsync(string region, string bucket, string key, CancellationToken cancellationToken);

    /// <summary>
    /// A stream of <paramref name="length"/> bytes from <paramref name="offset"/> (the whole object when the length
    /// is 0). With an <paramref name="eTag"/>, fails if the object changed since it was listed.
    /// </summary>
    Task<Stream> OpenReadAsync(string region, string bucket, string key, long offset, long length, string? eTag, CancellationToken cancellationToken);

    /// <summary>Uploads a local file as one object; <paramref name="sent"/> gets the bytes sent as they go.</summary>
    Task PutObjectAsync(string region, string bucket, string key, string path, Action<long> sent, CancellationToken cancellationToken);

    /// <summary>A zero-byte object, e.g. the "folder/" marker of an empty folder.</summary>
    Task PutEmptyObjectAsync(string region, string bucket, string key, CancellationToken cancellationToken);

    /// <summary>Starts a multipart upload; returns its upload id.</summary>
    Task<string> StartMultipartUploadAsync(string region, string bucket, string key, CancellationToken cancellationToken);

    /// <summary>Uploads <paramref name="length"/> bytes of a local file from <paramref name="offset"/> as part <paramref name="partNumber"/> (from 1); returns its ETag.</summary>
    Task<string> UploadPartAsync(string region, string bucket, string key, string uploadId, int partNumber, string path, long offset, long length, Action<long> sent, CancellationToken cancellationToken);

    /// <summary>Joins the parts (their ETags in part order) into the object.</summary>
    Task CompleteMultipartUploadAsync(string region, string bucket, string key, string uploadId, IReadOnlyList<string> eTags, CancellationToken cancellationToken);

    /// <summary>Drops an unfinished multipart upload and its parts.</summary>
    Task AbortMultipartUploadAsync(string region, string bucket, string key, string uploadId, CancellationToken cancellationToken);

    /// <summary>Deletes up to 1000 keys at once; returns the keys S3 could not delete.</summary>
    Task<IReadOnlyList<S3DeleteFailure>> DeleteObjectsAsync(string region, string bucket, IReadOnlyList<string> keys, CancellationToken cancellationToken);
}

/// <summary>
/// The real S3 client: one <see cref="AmazonS3Client"/> per region, so buckets in every commercial region work from
/// one panel.
/// </summary>
internal sealed class S3Api : IS3Api
{
    private const int PageSize = 1000;

    private readonly AwsSession _session;
    private readonly AWSCredentials? _credentials;
    private readonly HttpClientFactory? _http;
    private readonly Dictionary<string, AmazonS3Client> _clients = new(StringComparer.Ordinal);

    public S3Api(AwsSession session)
        : this(session, null, null)
    {
    }

    /// <summary>For tests: fixed credentials and an HTTP stand-in instead of the profile and the network.</summary>
    internal S3Api(AwsSession session, AWSCredentials? credentials, HttpClientFactory? http)
    {
        _session = session;
        _credentials = credentials;
        _http = http;
    }

    public async IAsyncEnumerable<IReadOnlyList<S3BucketInfo>> ListBucketsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? next = null;
        do
        {
            // With MaxBuckets the answer is paginated and names each bucket's region.
            var request = new ListBucketsRequest { MaxBuckets = PageSize, ContinuationToken = next };
            var response = await Client(_session.Region).ListBucketsAsync(request, cancellationToken).ConfigureAwait(false);
            yield return (response.Buckets ?? []).Select(b => new S3BucketInfo(b.BucketName, Utc(b.CreationDate), string.IsNullOrEmpty(b.BucketRegion) ? null : b.BucketRegion)).ToList();
            next = response.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(next));
    }

    public async Task<string> GetBucketLocationAsync(string bucket, CancellationToken cancellationToken)
    {
        var response = await Client(_session.Region).GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket }, cancellationToken).ConfigureAwait(false);
        return response.Location?.Value ?? string.Empty;
    }

    public async IAsyncEnumerable<S3Page> ListObjectsAsync(string region, string bucket, string prefix, bool recursive, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? next = null;
        do
        {
            // URL encoding lets S3 return keys with characters XML cannot carry (control characters); the SDK
            // leaves the decoding to the caller.
            var request = new ListObjectsV2Request { BucketName = bucket, MaxKeys = PageSize, Encoding = EncodingType.Url, ContinuationToken = next };
            if (prefix.Length > 0)
            {
                request.Prefix = prefix;
            }

            if (!recursive)
            {
                request.Delimiter = "/";
            }

            var response = await Client(region).ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            yield return new S3Page(
                (response.CommonPrefixes ?? []).Select(Decode).ToList(),
                (response.S3Objects ?? []).Select(o => new S3ObjectInfo(Decode(o.Key), o.Size ?? 0, Utc(o.LastModified), o.ETag, o.StorageClass?.Value)).ToList());
            next = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (!string.IsNullOrEmpty(next));
    }

    public async Task<S3ObjectInfo?> HeadObjectAsync(string region, string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await Client(region).GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = PathKey(key) }, cancellationToken).ConfigureAwait(false);
            return new S3ObjectInfo(key, response.ContentLength, Utc(response.LastModified), response.ETag, response.StorageClass?.Value);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string region, string bucket, string key, long offset, long length, string? eTag, CancellationToken cancellationToken)
    {
        var request = new GetObjectRequest { BucketName = bucket, Key = PathKey(key) };
        if (length > 0)
        {
            request.ByteRange = new ByteRange(offset, offset + length - 1);
        }

        if (!string.IsNullOrEmpty(eTag))
        {
            request.EtagToMatch = eTag;
        }

        var response = await Client(region).GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
        return response.ResponseStream;
    }

    public async Task PutObjectAsync(string region, string bucket, string key, string path, Action<long> sent, CancellationToken cancellationToken)
    {
        var request = new PutObjectRequest { BucketName = bucket, Key = PathKey(key), FilePath = path };
        request.StreamTransferProgress += (_, e) => sent(e.IncrementTransferred);
        await Client(region).PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task PutEmptyObjectAsync(string region, string bucket, string key, CancellationToken cancellationToken) =>
        Client(region).PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = PathKey(key), ContentBody = string.Empty }, cancellationToken);

    public async Task<string> StartMultipartUploadAsync(string region, string bucket, string key, CancellationToken cancellationToken)
    {
        var response = await Client(region).InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = PathKey(key) }, cancellationToken).ConfigureAwait(false);
        return response.UploadId;
    }

    public async Task<string> UploadPartAsync(string region, string bucket, string key, string uploadId, int partNumber, string path, long offset, long length, Action<long> sent, CancellationToken cancellationToken)
    {
        var request = new UploadPartRequest
        {
            BucketName = bucket,
            Key = PathKey(key),
            UploadId = uploadId,
            PartNumber = partNumber,
            FilePath = path,
            FilePosition = offset,
            PartSize = length,
        };
        request.StreamTransferProgress += (_, e) => sent(e.IncrementTransferred);
        var response = await Client(region).UploadPartAsync(request, cancellationToken).ConfigureAwait(false);
        return response.ETag;
    }

    public Task CompleteMultipartUploadAsync(string region, string bucket, string key, string uploadId, IReadOnlyList<string> eTags, CancellationToken cancellationToken) =>
        Client(region).CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = PathKey(key),
            UploadId = uploadId,
            PartETags = eTags.Select((eTag, i) => new PartETag(i + 1, eTag)).ToList(),
        }, cancellationToken);

    public Task AbortMultipartUploadAsync(string region, string bucket, string key, string uploadId, CancellationToken cancellationToken) =>
        Client(region).AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = PathKey(key), UploadId = uploadId }, cancellationToken);

    public async Task<IReadOnlyList<S3DeleteFailure>> DeleteObjectsAsync(string region, string bucket, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        // The keys travel in the request body, so they are not adjusted like the keys in paths.
        var request = new DeleteObjectsRequest { BucketName = bucket, Quiet = true, Objects = keys.Select(k => new KeyVersion { Key = k }).ToList() };
        try
        {
            var response = await Client(region).DeleteObjectsAsync(request, cancellationToken).ConfigureAwait(false);
            return Failures(response);
        }
        catch (DeleteObjectsException ex)
        {
            // The SDK throws when some keys failed; the response lists them.
            return Failures(ex.Response);
        }
    }

    public void Dispose()
    {
        lock (_clients)
        {
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }

            _clients.Clear();
        }
    }

    /// <summary>
    /// The AWS SDK for .NET drops one leading "/" from a key it puts in the request path, so "/a" would reach S3
    /// as "a". Doubling that slash keeps the key exact.
    /// </summary>
    internal static string PathKey(string key) => key.StartsWith('/') ? "/" + key : key;

    /// <summary>Keys listed with URL encoding: "+" is a space, "%XX" a UTF-8 byte.</summary>
    internal static string Decode(string text) => WebUtility.UrlDecode(text);

    private static List<S3DeleteFailure> Failures(DeleteObjectsResponse? response) =>
        (response?.DeleteErrors ?? []).Select(e => new S3DeleteFailure(e.Key, e.Code ?? string.Empty, e.Message ?? string.Empty)).ToList();

    private static DateTime? Utc(DateTime? time) => time switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } utc => utc,
        { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
        { } other => DateTime.SpecifyKind(other, DateTimeKind.Utc),
    };

    private AmazonS3Client Client(string region)
    {
        lock (_clients)
        {
            if (!_clients.TryGetValue(region, out var client))
            {
                var config = _session.Configure(new AmazonS3Config());
                config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
                if (_http is not null)
                {
                    config.HttpClientFactory = _http;
                }

                client = new AmazonS3Client(_credentials ?? _session.Credentials, config);
                _clients[region] = client;
            }

            return client;
        }
    }
}

/// <summary>Bucket locations as region names.</summary>
internal static class BucketRegions
{
    /// <summary>"" (and null) is us-east-1 and "EU" is eu-west-1, for historical reasons; others are region names.</summary>
    public static string FromLocation(string? location) => location switch
    {
        null or "" or "US" => "us-east-1",
        "EU" => "eu-west-1",
        _ => location,
    };
}
