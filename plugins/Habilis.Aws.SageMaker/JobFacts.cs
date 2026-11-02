using System.Globalization;
using Amazon.SageMaker.Model;

namespace Habilis.Aws.SageMaker;

/// <summary>An instance type and how many instances of it a job uses.</summary>
internal readonly record struct InstanceUse(string Type, int Count)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Count} × {Type}");
}

/// <summary>What the panel and the views derive from a job's description: state, times, instances.</summary>
internal static class JobFacts
{
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Stopping = "Stopping";
    public const string Stopped = "Stopped";

    public static string Status(DescribeTrainingJobResponse job) => job.TrainingJobStatus?.Value ?? string.Empty;

    public static string SecondaryStatus(DescribeTrainingJobResponse job) => job.SecondaryStatus?.Value ?? string.Empty;

    /// <summary>Still running: shown above finished jobs.</summary>
    public static bool IsRunning(DescribeTrainingJobResponse job) => Status(job) is InProgress or Stopping;

    /// <summary>The time from creation to the end (or to now while running), as the console's Duration column.</summary>
    public static TimeSpan? Duration(DescribeTrainingJobResponse job, DateTime now)
    {
        if (job.CreationTime is not { } created)
        {
            return null;
        }

        var end = job.TrainingEndTime ?? (IsRunning(job) ? now : job.LastModifiedTime ?? now);
        var duration = Utc(end) - Utc(created);
        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }

    /// <summary>How long an available warm pool is kept: the keep-alive period from the job's end, or null when there is no such pool.</summary>
    public static TimeSpan? WarmPoolLeft(DescribeTrainingJobResponse job, DateTime now)
    {
        if (job.WarmPoolStatus?.Status?.Value != "Available" || job.ResourceConfig?.KeepAlivePeriodInSeconds is not { } keepAlive)
        {
            return null;
        }

        if ((job.TrainingEndTime ?? job.LastModifiedTime) is not { } end)
        {
            return null;
        }

        var left = Utc(end) + TimeSpan.FromSeconds(keepAlive) - Utc(now);
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    /// <summary>
    /// The instances the job runs on: every instance group of a heterogeneous cluster, else the instance type and
    /// count (the selected ones when SageMaker picked among several preferences). Empty when the job reports none.
    /// </summary>
    public static IReadOnlyList<InstanceUse> Instances(DescribeTrainingJobResponse job)
    {
        var config = job.ResourceConfig;
        if (config is null)
        {
            return [];
        }

        if (config.InstanceGroups is { Count: > 0 } groups)
        {
            return groups
                .Where(g => !string.IsNullOrEmpty(g.InstanceType?.Value))
                .Select(g => new InstanceUse(g.InstanceType!.Value, g.InstanceCount ?? 1))
                .ToList();
        }

        var type = config.SelectedInstanceType?.Value is { Length: > 0 } selected ? selected : config.InstanceType?.Value;
        return string.IsNullOrEmpty(type) ? [] : [new InstanceUse(type, config.SelectedInstanceCount ?? config.InstanceCount ?? 1)];
    }

    /// <summary>A time from the SDK as UTC (it may come as local or unspecified).</summary>
    public static DateTime Utc(DateTime time) => time.Kind switch
    {
        DateTimeKind.Local => time.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(time, DateTimeKind.Utc),
        _ => time,
    };

    /// <summary>A time in the user's local time zone, e.g. "2025-03-01 14:05:09".</summary>
    public static string Local(DateTime time) => Utc(time).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
