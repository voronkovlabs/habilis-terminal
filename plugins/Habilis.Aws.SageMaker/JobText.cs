using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SageMaker.Model;
using Habilis.Aws.Common;

namespace Habilis.Aws.SageMaker;

/// <summary>The texts made from a job's description: the readable summary (F3) and the full record as JSON (Shift+F3, F5).</summary>
internal static class JobText
{
    private const int LabelWidth = 24;

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,

        // The record is read in the viewer or a file, never embedded in HTML: keep "+", "<" and non-ASCII readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The readable summary. Its first section depends on the state: progress for a running job, outputs for a
    /// completed (or stopped) one, the failure reason for a failed one. Then the details and the cost estimate.
    /// </summary>
    public static string Summary(DescribeTrainingJobResponse job, CostEstimate cost, DateTime now)
    {
        var text = new StringBuilder();
        Row(text, "Training job", job.TrainingJobName);
        Row(text, "Status", JobFacts.SecondaryStatus(job) is { Length: > 0 } secondary ? $"{JobFacts.Status(job)} · {secondary}" : JobFacts.Status(job));
        text.AppendLine();

        switch (JobFacts.Status(job))
        {
            case JobFacts.Completed:
            case JobFacts.Stopped:
                Outputs(text, job);
                break;
            case JobFacts.Failed:
                Failure(text, job);
                break;
            default:
                Progress(text, job, now);
                break;
        }

        Details(text, job, now);
        Cost(text, cost);
        return text.ToString();
    }

    /// <summary>The complete description as indented JSON, with the API's field names and without the SDK's response metadata.</summary>
    public static string FullRecord(DescribeTrainingJobResponse job) => (ToJson(job) ?? new JsonObject()).ToJsonString(Indented);

    private static void Progress(StringBuilder text, DescribeTrainingJobResponse job, DateTime now)
    {
        text.AppendLine("PROGRESS");
        var transitions = job.SecondaryStatusTransitions ?? [];
        if (transitions.Count == 0)
        {
            text.AppendLine("  No status transitions reported yet.");
        }

        foreach (var step in transitions)
        {
            var started = step.StartTime is { } start ? JobFacts.Local(start) : "?";
            var took = step.StartTime is { } from ? AwsText.Duration(JobFacts.Utc(step.EndTime ?? now) - JobFacts.Utc(from)) : string.Empty;
            var current = step.EndTime is null ? " (current)" : string.Empty;
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {step.Status?.Value,-22}{started}  {took,-14}{current}").TrimEnd());
            if (!string.IsNullOrWhiteSpace(step.StatusMessage))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {string.Empty,-22}{step.StatusMessage.Trim()}");
            }
        }

        if (JobFacts.Duration(job, now) is { } elapsed)
        {
            text.AppendLine();
            Row(text, "Elapsed since creation", AwsText.Duration(elapsed));
        }

        if (job.StoppingCondition?.MaxRuntimeInSeconds is { } maxRuntime)
        {
            Row(text, "Maximum runtime", AwsText.Duration(TimeSpan.FromSeconds(maxRuntime)));
        }

        text.AppendLine();
    }

    private static void Outputs(StringBuilder text, DescribeTrainingJobResponse job)
    {
        text.AppendLine("OUTPUTS");
        if (JobFacts.Status(job) == JobFacts.Stopped)
        {
            text.AppendLine("  The job was stopped before it finished; outputs may be incomplete.");
        }

        Row(text, "Model artifacts", job.ModelArtifacts?.S3ModelArtifacts is { Length: > 0 } artifacts ? artifacts : "(none reported)");
        if (!string.IsNullOrEmpty(job.OutputModelPackageArn))
        {
            Row(text, "Model package", job.OutputModelPackageArn);
        }

        var metrics = job.FinalMetricDataList ?? [];
        if (metrics.Count == 0)
        {
            Row(text, "Final metrics", "(none reported)");
        }
        else
        {
            text.AppendLine("  Final metrics:");
            foreach (var metric in metrics.OrderBy(m => m.MetricName, StringComparer.Ordinal))
            {
                var value = metric.Value is { } number ? number.ToString(CultureInfo.InvariantCulture) : "?";
                text.AppendLine(CultureInfo.InvariantCulture, $"    {metric.MetricName,-30} {value}");
            }
        }

        if (!string.IsNullOrEmpty(job.FailureReason))
        {
            Row(text, "Reason", job.FailureReason);
        }

        text.AppendLine();
    }

    private static void Failure(StringBuilder text, DescribeTrainingJobResponse job)
    {
        text.AppendLine("FAILURE");
        text.AppendLine(CultureInfo.InvariantCulture, $"  {(string.IsNullOrWhiteSpace(job.FailureReason) ? "SageMaker reported no failure reason." : job.FailureReason.Trim())}");
        if (job.SecondaryStatusTransitions is { Count: > 0 } transitions && transitions[^1] is { } last)
        {
            Row(text, "Last step", string.IsNullOrWhiteSpace(last.StatusMessage) ? last.Status?.Value : $"{last.Status?.Value}: {last.StatusMessage.Trim()}");
        }

        text.AppendLine();
    }

    private static void Details(StringBuilder text, DescribeTrainingJobResponse job, DateTime now)
    {
        text.AppendLine("DETAILS");
        Row(text, "ARN", job.TrainingJobArn);
        Row(text, "Created", Time(job.CreationTime));
        Row(text, "Training started", Time(job.TrainingStartTime));
        Row(text, "Training ended", Time(job.TrainingEndTime));
        Row(text, "Last modified", Time(job.LastModifiedTime));
        Row(text, "Duration", JobFacts.Duration(job, now) is { } duration ? AwsText.Duration(duration) : null);
        Row(text, "Training time", Seconds(job.TrainingTimeInSeconds));
        Row(text, "Billable time", Seconds(job.BillableTimeInSeconds));
        var instances = JobFacts.Instances(job);
        Row(text, "Instances", instances.Count > 0 ? string.Join(", ", instances) : "(none reported)");
        Row(text, "Volume", job.ResourceConfig?.VolumeSizeInGB is { } volume ? string.Create(CultureInfo.InvariantCulture, $"{volume} GB") : null);
        Row(text, "Managed spot training", job.EnableManagedSpotTraining == true ? "yes" : "no");
        Row(text, "Maximum runtime", Seconds(job.StoppingCondition?.MaxRuntimeInSeconds));
        Row(text, "Maximum wait", Seconds(job.StoppingCondition?.MaxWaitTimeInSeconds));
        Row(text, "Algorithm", job.AlgorithmSpecification?.AlgorithmName);
        Row(text, "Training image", job.AlgorithmSpecification?.TrainingImage);
        Row(text, "Input mode", job.AlgorithmSpecification?.TrainingInputMode?.Value);
        Row(text, "Input channels", job.InputDataConfig is { Count: > 0 } channels ? string.Join(", ", channels.Select(c => c.ChannelName)) : null);
        Row(text, "Output path", job.OutputDataConfig?.S3OutputPath);
        Row(text, "Role", job.RoleArn);
        if (job.WarmPoolStatus is { } pool)
        {
            Row(text, "Warm pool", pool.Status?.Value);
            Row(text, "Warm pool kept for", Seconds(job.ResourceConfig?.KeepAlivePeriodInSeconds));
            Row(text, "Warm pool time left", JobFacts.WarmPoolLeft(job, now) is { } left ? AwsText.Duration(left) : null);
            Row(text, "Warm pool billable", Seconds(pool.ResourceRetainedBillableTimeInSeconds));
            Row(text, "Warm pool reused by", pool.ReusedByJob);
        }

        Row(text, "Tuning job", job.TuningJobArn);
        Row(text, "AutoML job", job.AutoMLJobArn);
        Row(text, "Labeling job", job.LabelingJobArn);
        if (job.HyperParameters is { Count: > 0 } parameters)
        {
            text.AppendLine("  Hyperparameters:");
            foreach (var (name, value) in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"    {name,-30} {value}");
            }
        }

        text.AppendLine();
    }

    private static void Cost(StringBuilder text, CostEstimate cost)
    {
        text.AppendLine("ESTIMATED COST");
        text.AppendLine(CultureInfo.InvariantCulture, $"  {cost.Text}");
        foreach (var line in cost.Lines)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {line}");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"  {cost.Statement}");
    }

    /// <summary>A "label: value" line; nothing when the job does not report the value.</summary>
    private static void Row(StringBuilder text, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {label + ":",-LabelWidth}{value}");
        }
    }

    private static string? Time(DateTime? time) => time is { } value ? JobFacts.Local(value) : null;

    private static string? Seconds(int? seconds) => seconds is { } value ? AwsText.Duration(TimeSpan.FromSeconds(value)) : null;

    /// <summary>
    /// A JSON node for an SDK model value: objects by their public properties (absent values left out), lists,
    /// maps, the SDK's string constants as strings, times as ISO 8601 UTC.
    /// </summary>
    internal static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        ConstantClass constant => JsonValue.Create(constant.Value),
        bool flag => JsonValue.Create(flag),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        float number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        DateTime time => JsonValue.Create(JobFacts.Utc(time).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
        Enum constant => JsonValue.Create(constant.ToString()),
        MemoryStream stream => JsonValue.Create(Convert.ToBase64String(stream.ToArray())),
        IDictionary map => MapToJson(map),
        IEnumerable list => new JsonArray(list.Cast<object?>().Select(ToJson).ToArray()),
        _ => ObjectToJson(value),
    };

    private static JsonObject MapToJson(IDictionary map)
    {
        var json = new JsonObject();
        foreach (DictionaryEntry pair in map)
        {
            json[Convert.ToString(pair.Key, CultureInfo.InvariantCulture) ?? string.Empty] = ToJson(pair.Value);
        }

        return json;
    }

    private static JsonObject ObjectToJson(object value)
    {
        var json = new JsonObject();
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Indexers and the SDK's own response data (metadata, HTTP status, content length) are not part of the record.
            if (property.GetIndexParameters().Length > 0 || property.DeclaringType == typeof(AmazonWebServiceResponse))
            {
                continue;
            }

            if (ToJson(property.GetValue(value)) is { } node)
            {
                json[property.Name] = node;
            }
        }

        return json;
    }
}
