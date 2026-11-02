using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using Amazon.S3;

namespace Habilis.Aws.S3.Tests;

/// <summary>
/// S3 in memory. Every call checks that it was sent to the bucket's own region, as the real service would refuse
/// it otherwise. Failures and pauses are scripted per test.
/// </summary>
internal sealed class FakeS3 : IS3Api
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Upload> _uploads = new(StringComparer.Ordinal);
    private int _version;
    private int _uploadIds;

    public int PageSize { get; set; } = 1000;

    public Exception? ListError { get; set; }

    /// <summary>Thrown by reads of these keys.</summary>
    public Dictionary<string, Exception> ReadErrors { get; } = new(StringComparer.Ordinal);

    /// <summary>Reads of this key send their first chunk, then wait until cancelled.</summary>
    public string? StallReadsOf { get; set; }

    public TaskCompletionSource ReadStalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int? FailPart { get; set; }

    public int? StallPart { get; set; }

    public TaskCompletionSource PartStalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>DeleteObjects reports these keys as not deleted (access denied).</summary>
    public HashSet<string> UndeletableKeys { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<(string Key, long Offset, long Length)> Reads { get; } = new();

    public ConcurrentQueue<string> Aborted { get; } = new();

    public ConcurrentQueue<int> DeleteBatchSizes { get; } = new();

    public int LocationCalls;

    public int OpenUploads
    {
        get
        {
            lock (_lock)
            {
                return _uploads.Count;
            }
        }
    }

    public FakeS3 AddBucket(string name, string location = "", bool listRegion = false)
    {
        lock (_lock)
        {
            _buckets[name] = new Bucket(location, listRegion);
        }

        return this;
    }

    public FakeS3 Add(string bucket, string key, byte[] data, string? storageClass = null)
    {
        lock (_lock)
        {
            if (!_buckets.TryGetValue(bucket, out var found))
            {
                found = _buckets[bucket] = new Bucket(string.Empty, false);
            }

            found.Objects[key] = new Item(data, $"\"etag-{++_version}\"", storageClass);
        }

        return this;
    }

    public FakeS3 Add(string bucket, string key, string text) => Add(bucket, key, System.Text.Encoding.UTF8.GetBytes(text));

    public byte[]? Data(string bucket, string key)
    {
        lock (_lock)
        {
            return _buckets[bucket].Objects.TryGetValue(key, out var item) ? item.Data : null;
        }
    }

    public IReadOnlyList<string> Keys(string bucket)
    {
        lock (_lock)
        {
            return _buckets[bucket].Objects.Keys.ToList();
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<S3BucketInfo>> ListBucketsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        List<S3BucketInfo> all;
        lock (_lock)
        {
            all = _buckets.OrderBy(b => b.Key, StringComparer.Ordinal)
                .Select(b => new S3BucketInfo(b.Key, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), b.Value.ListRegion ? BucketRegions.FromLocation(b.Value.Location) : null))
                .ToList();
        }

        for (var i = 0; i < all.Count; i += PageSize)
        {
            yield return all.Skip(i).Take(PageSize).ToList();
        }
    }

    public Task<string> GetBucketLocationAsync(string bucket, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref LocationCalls);
        lock (_lock)
        {
            return _buckets.TryGetValue(bucket, out var found)
                ? Task.FromResult(found.Location)
                : Task.FromException<string>(new AmazonS3Exception("The specified bucket does not exist") { ErrorCode = "NoSuchBucket", StatusCode = HttpStatusCode.NotFound });
        }
    }

    public async IAsyncEnumerable<S3Page> ListObjectsAsync(string region, string bucket, string prefix, bool recursive, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (ListError is not null)
        {
            throw ListError;
        }

        var items = new List<(string? Prefix, S3ObjectInfo? Info)>();
        lock (_lock)
        {
            string? lastPrefix = null;
            foreach (var (key, item) in Find(region, bucket).Objects.Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var slash = key.IndexOf('/', prefix.Length);
                if (!recursive && slash >= 0)
                {
                    var common = key[..(slash + 1)];
                    if (common != lastPrefix)
                    {
                        items.Add((common, null));
                        lastPrefix = common;
                    }

                    continue;
                }

                items.Add((null, Info(key, item)));
            }
        }

        // S3 counts common prefixes and keys together towards a page.
        for (var i = 0; i < items.Count; i += PageSize)
        {
            var page = items.Skip(i).Take(PageSize).ToList();
            yield return new S3Page(page.Select(p => p.Prefix).OfType<string>().ToList(), page.Select(p => p.Info).OfType<S3ObjectInfo>().ToList());
        }
    }

    public Task<S3ObjectInfo?> HeadObjectAsync(string region, string bucket, string key, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Find(region, bucket).Objects.TryGetValue(key, out var item) ? Info(key, item) : null);
        }
    }

    public Task<Stream> OpenReadAsync(string region, string bucket, string key, long offset, long length, string? eTag, CancellationToken cancellationToken)
    {
        Reads.Enqueue((key, offset, length));
        if (ReadErrors.TryGetValue(key, out var error))
        {
            return Task.FromException<Stream>(error);
        }

        Item item;
        lock (_lock)
        {
            if (!Find(region, bucket).Objects.TryGetValue(key, out item!))
            {
                return Task.FromException<Stream>(new AmazonS3Exception("The specified key does not exist.") { ErrorCode = "NoSuchKey", StatusCode = HttpStatusCode.NotFound });
            }
        }

        if (eTag is not null && eTag != item.ETag)
        {
            return Task.FromException<Stream>(new AmazonS3Exception("At least one of the pre-conditions you specified did not hold") { ErrorCode = "PreconditionFailed", StatusCode = HttpStatusCode.PreconditionFailed });
        }

        var slice = length == 0 ? item.Data : item.Data.AsSpan((int)offset, (int)length).ToArray();
        return Task.FromResult<Stream>(key == StallReadsOf ? new StallingStream(slice, ReadStalled) : new MemoryStream(slice));
    }

    public Task PutObjectAsync(string region, string bucket, string key, string path, Action<long> sent, CancellationToken cancellationToken)
    {
        var data = File.ReadAllBytes(path);
        sent(data.Length);
        Store(region, bucket, key, data);
        return Task.CompletedTask;
    }

    public Task PutEmptyObjectAsync(string region, string bucket, string key, CancellationToken cancellationToken)
    {
        Store(region, bucket, key, []);
        return Task.CompletedTask;
    }

    public Task<string> StartMultipartUploadAsync(string region, string bucket, string key, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Find(region, bucket);
            var id = $"upload-{++_uploadIds}";
            _uploads[id] = new Upload(bucket, key);
            return Task.FromResult(id);
        }
    }

    public async Task<string> UploadPartAsync(string region, string bucket, string key, string uploadId, int partNumber, string path, long offset, long length, Action<long> sent, CancellationToken cancellationToken)
    {
        if (partNumber == StallPart)
        {
            PartStalled.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (partNumber == FailPart)
        {
            throw new AmazonS3Exception("We encountered an internal error. Please try again.") { ErrorCode = "InternalError", StatusCode = HttpStatusCode.InternalServerError };
        }

        var data = new byte[length];
        using (var file = File.OpenRead(path))
        {
            file.Position = offset;
            file.ReadExactly(data);
        }

        sent(length / 2);
        sent(length);
        lock (_lock)
        {
            _uploads[uploadId].Parts[partNumber] = data;
        }

        return $"\"part-{partNumber}\"";
    }

    public Task CompleteMultipartUploadAsync(string region, string bucket, string key, string uploadId, IReadOnlyList<string> eTags, CancellationToken cancellationToken)
    {
        Upload upload;
        lock (_lock)
        {
            upload = _uploads[uploadId];
            _uploads.Remove(uploadId);
        }

        Assert.Equal(Enumerable.Range(1, eTags.Count).Select(n => $"\"part-{n}\""), eTags);
        Store(region, bucket, key, upload.Parts.OrderBy(p => p.Key).SelectMany(p => p.Value).ToArray());
        return Task.CompletedTask;
    }

    public Task AbortMultipartUploadAsync(string region, string bucket, string key, string uploadId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _uploads.Remove(uploadId);
        }

        Aborted.Enqueue(uploadId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<S3DeleteFailure>> DeleteObjectsAsync(string region, string bucket, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        Assert.InRange(keys.Count, 1, 1000);
        DeleteBatchSizes.Enqueue(keys.Count);
        var failures = new List<S3DeleteFailure>();
        lock (_lock)
        {
            var objects = Find(region, bucket).Objects;
            foreach (var key in keys)
            {
                if (UndeletableKeys.Contains(key))
                {
                    failures.Add(new S3DeleteFailure(key, "AccessDenied", "Access Denied"));
                }
                else
                {
                    objects.Remove(key);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<S3DeleteFailure>>(failures);
    }

    public void Dispose()
    {
    }

    private static S3ObjectInfo Info(string key, Item item) =>
        new(key, item.Data.Length, new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc), item.ETag, item.StorageClass ?? "STANDARD");

    private void Store(string region, string bucket, string key, byte[] data)
    {
        lock (_lock)
        {
            Find(region, bucket).Objects[key] = new Item(data, $"\"etag-{++_version}\"", null);
        }
    }

    private Bucket Find(string region, string bucket)
    {
        var found = _buckets[bucket];
        var expected = BucketRegions.FromLocation(found.Location);
        if (region != expected)
        {
            throw new AmazonS3Exception($"The bucket is in this region: {expected}.") { ErrorCode = "PermanentRedirect", StatusCode = HttpStatusCode.MovedPermanently };
        }

        return found;
    }

    private sealed record Item(byte[] Data, string ETag, string? StorageClass);

    private sealed class Bucket(string location, bool listRegion)
    {
        public string Location { get; } = location;

        public bool ListRegion { get; } = listRegion;

        public SortedDictionary<string, Item> Objects { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Upload(string bucket, string key)
    {
        public string Bucket { get; } = bucket;

        public string Key { get; } = key;

        public Dictionary<int, byte[]> Parts { get; } = [];
    }

    /// <summary>Sends the first 1000 bytes, then waits until the read is cancelled.</summary>
    private sealed class StallingStream(byte[] data, TaskCompletionSource stalled) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= 1000)
            {
                stalled.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return await base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, 1000 - Position)], cancellationToken);
        }
    }
}
