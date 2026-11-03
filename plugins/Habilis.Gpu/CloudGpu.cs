using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.SageMaker;
using Amazon.SageMaker.Model;
using Habilis.Aws.Common;

namespace Habilis.Gpu;

/// <summary>A running training job: its name and the hosts SageMaker names <c>&lt;job&gt;/algo-N</c>.</summary>
internal sealed record CloudJob(string Name, string InstanceType, int InstanceCount, DateTime? Started)
{
    public IReadOnlyList<string> Hosts => [.. Enumerable.Range(1, Math.Max(1, InstanceCount)).Select(i => $"{Name}/algo-{i}")];
}

/// <summary>One minute of one host's instance metrics; GPU values are summed over the host's GPUs (4 GPUs: up to 400%).</summary>
internal sealed record HostMetrics(string Host, IReadOnlyList<double?> Gpu, IReadOnlyList<double?> GpuMemory, IReadOnlyList<double?> Cpu, IReadOnlyList<double?> Memory)
{
    public static double? Last(IReadOnlyList<double?> series) => series.LastOrDefault(v => v is not null);
}

/// <summary>The SageMaker and CloudWatch calls of the SageMaker view (read-only); a fake stands in for it in the tests.</summary>
internal interface ICloudGpuApi : IDisposable
{
    Task<IReadOnlyList<CloudJob>> RunningJobsAsync(CancellationToken cancellationToken);

    /// <summary>The last <paramref name="minutes"/> minutes of each host's metrics, one value per minute (null: no data).</summary>
    Task<IReadOnlyList<HostMetrics>> MetricsAsync(CloudJob job, int minutes, DateTime now, CancellationToken cancellationToken);
}

/// <summary>
/// Training jobs in progress (SageMaker) and their instance metrics (CloudWatch namespace
/// <c>/aws/sagemaker/TrainingJobs</c>, dimension Host): GPUUtilization, GPUMemoryUtilization, CPUUtilization,
/// MemoryUtilization.
/// </summary>
internal sealed class CloudGpuApi(AwsSession session) : ICloudGpuApi
{
    public const string Namespace = "/aws/sagemaker/TrainingJobs";
    private static readonly string[] Metrics = ["GPUUtilization", "GPUMemoryUtilization", "CPUUtilization", "MemoryUtilization"];

    private readonly AmazonSageMakerClient _sageMaker = new(session.Credentials, session.Configure(new AmazonSageMakerConfig()));
    private readonly AmazonCloudWatchClient _cloudWatch = new(session.Credentials, session.Configure(new AmazonCloudWatchConfig()));

    public async Task<IReadOnlyList<CloudJob>> RunningJobsAsync(CancellationToken cancellationToken)
    {
        var jobs = new List<CloudJob>();
        var list = await _sageMaker.ListTrainingJobsAsync(new ListTrainingJobsRequest { StatusEquals = TrainingJobStatus.InProgress, MaxResults = 50, SortBy = SortBy.CreationTime, SortOrder = SortOrder.Descending }, cancellationToken).ConfigureAwait(false);
        foreach (var summary in list.TrainingJobSummaries ?? [])
        {
            var job = await _sageMaker.DescribeTrainingJobAsync(new DescribeTrainingJobRequest { TrainingJobName = summary.TrainingJobName }, cancellationToken).ConfigureAwait(false);
            var config = job.ResourceConfig;
            var groups = config?.InstanceGroups ?? [];
            var type = config?.InstanceType?.Value ?? groups.FirstOrDefault()?.InstanceType?.Value ?? "?";
            var count = config?.InstanceCount ?? groups.Sum(g => g.InstanceCount ?? 0);
            jobs.Add(new CloudJob(job.TrainingJobName, type, count, job.TrainingStartTime));
        }

        return jobs;
    }

    public async Task<IReadOnlyList<HostMetrics>> MetricsAsync(CloudJob job, int minutes, DateTime now, CancellationToken cancellationToken)
    {
        var end = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc);
        var start = end.AddMinutes(-minutes);
        var queries = new List<MetricDataQuery>();
        var hosts = job.Hosts;
        for (var h = 0; h < hosts.Count; h++)
        {
            for (var m = 0; m < Metrics.Length; m++)
            {
                queries.Add(new MetricDataQuery
                {
                    Id = $"h{h}m{m}",
                    MetricStat = new MetricStat
                    {
                        Metric = new Metric { Namespace = Namespace, MetricName = Metrics[m], Dimensions = [new Dimension { Name = "Host", Value = hosts[h] }] },
                        Period = 60,
                        Stat = "Average",
                    },
                });
            }
        }

        var values = new Dictionary<string, Dictionary<DateTime, double>>();
        string? next = null;
        do
        {
            var response = await _cloudWatch.GetMetricDataAsync(new GetMetricDataRequest { StartTime = start, EndTime = end, MetricDataQueries = queries, NextToken = next }, cancellationToken).ConfigureAwait(false);
            foreach (var result in response.MetricDataResults ?? [])
            {
                var series = values.TryGetValue(result.Id, out var existing) ? existing : values[result.Id] = [];
                var stamps = result.Timestamps ?? [];
                var numbers = result.Values ?? [];
                for (var i = 0; i < Math.Min(stamps.Count, numbers.Count); i++)
                {
                    series[stamps[i].ToUniversalTime()] = numbers[i];
                }
            }

            next = response.NextToken;
        }
        while (!string.IsNullOrEmpty(next));

        IReadOnlyList<double?> Series(int h, int m) =>
            [.. Enumerable.Range(0, minutes).Select(i => values.TryGetValue($"h{h}m{m}", out var s) && s.TryGetValue(start.AddMinutes(i), out var v) ? v : (double?)null)];

        return [.. hosts.Select((host, h) => new HostMetrics(host, Series(h, 0), Series(h, 1), Series(h, 2), Series(h, 3)))];
    }

    public void Dispose()
    {
        _sageMaker.Dispose();
        _cloudWatch.Dispose();
    }
}
