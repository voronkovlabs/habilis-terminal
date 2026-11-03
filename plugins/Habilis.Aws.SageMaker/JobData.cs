using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SageMaker.Model;
using Habilis.Aws.Common;

namespace Habilis.Aws.SageMaker;

/// <summary>An S3 location a job wrote or ran from, and the job folder's subfolder it is copied to.</summary>
internal sealed record JobDataSource(string Folder, string Bucket, string Key, bool IsPrefix)
{
    public string Uri => $"s3://{Bucket}/{Key}";

    public bool Covers(JobDataSource other) =>
        IsPrefix && other != this && Bucket == other.Bucket && other.Key.StartsWith(Key, StringComparison.Ordinal);
}

/// <summary>
/// The S3 data F5 copies with a job (issue #68): its output (model artifacts, output data, debugger and profiler
/// output under <c>&lt;output path&gt;/&lt;job&gt;/</c>), checkpoints, TensorBoard output and the source code
/// (<c>sagemaker_submit_directory</c>). Input channels are not copied. A location inside another one is copied once.
/// </summary>
internal static class JobData
{
    public static IReadOnlyList<JobDataSource> Sources(DescribeTrainingJobResponse job)
    {
        var name = job.TrainingJobName ?? string.Empty;
        var sources = new List<JobDataSource>();
        void Add(string folder, string? uri, bool prefix)
        {
            if (Parse(uri) is { } location)
            {
                var key = prefix && location.Key.Length > 0 ? location.Key.TrimEnd('/') + "/" : location.Key;
                sources.Add(new JobDataSource(folder, location.Bucket, key, prefix));
            }
        }

        // Output paths are shared by many jobs: only this job's part of them.
        Add("output", Under(job.OutputDataConfig?.S3OutputPath, name), prefix: true);
        Add("model", job.ModelArtifacts?.S3ModelArtifacts, prefix: false);
        Add("checkpoints", job.CheckpointConfig?.S3Uri, prefix: true);
        Add("tensorboard", Under(job.TensorBoardOutputConfig?.S3OutputPath, name), prefix: true);
        Add("debugger", Under(job.DebugHookConfig?.S3OutputPath, name), prefix: true);
        Add("profiler", Under(job.ProfilerConfig?.S3OutputPath, name), prefix: true);
        if (job.HyperParameters is { } parameters && parameters.TryGetValue("sagemaker_submit_directory", out var code))
        {
            Add("code", code.Trim('"'), prefix: !code.Trim('"').EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase));
        }

        return sources.Where(s => !sources.Any(other => other.Covers(s)))
            .DistinctBy(s => (s.Bucket, s.Key))
            .ToList();
    }

    /// <summary>The path when it already names the job, else the job's folder under it.</summary>
    private static string? Under(string? path, string jobName) =>
        string.IsNullOrEmpty(path) ? null
        : path.Contains(jobName, StringComparison.Ordinal) ? path
        : path.TrimEnd('/') + "/" + jobName + "/";

    /// <summary>"s3://bucket/key" → (bucket, key); null for anything else.</summary>
    internal static (string Bucket, string Key)? Parse(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = uri[5..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        return slash switch
        {
            0 => null,
            < 0 => (rest, string.Empty),
            _ => (rest[..slash], rest[(slash + 1)..]),
        };
    }
}

/// <summary>The S3 calls F5 makes for job data (read-only); a fake stands in for it in the tests.</summary>
internal interface IJobDataStore : IDisposable
{
    /// <summary>The objects under a prefix (key and size), or the single object for a key.</summary>
    IAsyncEnumerable<(string Key, long Size)> ListAsync(JobDataSource source, CancellationToken cancellationToken);

    /// <summary>Writes the object to <paramref name="target"/> (a new file), reporting bytes as they arrive.</summary>
    Task DownloadAsync(string bucket, string key, string target, Action<long> bytes, CancellationToken cancellationToken);
}

/// <summary>Amazon S3 in the job's region (SageMaker requires its buckets there).</summary>
internal sealed class JobDataStore(AwsSession session) : IJobDataStore
{
    private readonly AmazonS3Client _client = new(session.Credentials, session.Configure(new AmazonS3Config()));

    public async IAsyncEnumerable<(string Key, long Size)> ListAsync(JobDataSource source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!source.IsPrefix)
        {
            var metadata = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = source.Bucket, Key = source.Key }, cancellationToken).ConfigureAwait(false);
            yield return (source.Key, metadata.ContentLength);
            yield break;
        }

        string? next = null;
        do
        {
            var response = await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = source.Bucket, Prefix = source.Key, ContinuationToken = next }, cancellationToken).ConfigureAwait(false);
            foreach (var item in response.S3Objects ?? [])
            {
                if (!item.Key.EndsWith('/'))
                {
                    yield return (item.Key, item.Size ?? 0);
                }
            }

            next = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (next is not null);
    }

    public async Task DownloadAsync(string bucket, string key, string target, Action<long> bytes, CancellationToken cancellationToken)
    {
        using var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, cancellationToken).ConfigureAwait(false);
        await using var input = response.ResponseStream;
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            bytes(read);
        }
    }

    public void Dispose() => _client.Dispose();
}
