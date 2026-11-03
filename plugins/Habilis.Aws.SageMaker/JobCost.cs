using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SageMaker.Model;
using Habilis.Aws.Common;

namespace Habilis.Aws.SageMaker;

/// <summary>
/// On-demand SageMaker training prices per instance-hour. The built-in table is an embedded <c>prices.json</c>; a
/// <c>prices.json</c> next to the plugin assembly replaces it, so a user can correct or extend the prices.
/// </summary>
internal sealed class PriceTable
{
    public const string FileName = "prices.json";

    private readonly Dictionary<string, decimal> _perHour;
    private readonly HashSet<string> _live = new(StringComparer.OrdinalIgnoreCase);
    private string _liveRegion = string.Empty;

    private PriceTable(Dictionary<string, decimal> perHour, string basis, string source, string? problem)
    {
        _perHour = perHour;
        Basis = basis;
        Source = source;
        Problem = problem;
    }

    /// <summary>Where the prices come from, e.g. "on-demand us-east-1 prices as of 2025-01".</summary>
    public string Basis { get; }

    /// <summary>"built-in table" or the path of the user's file.</summary>
    public string Source { get; }

    /// <summary>Why the user's file was not used, when it could not be read; null otherwise.</summary>
    public string? Problem { get; }

    public int Count => _perHour.Count;

    /// <summary>The sentence every estimate carries.</summary>
    public string Statement => $"Estimate from {Basis}; managed spot, discounts and other regions differ.";

    public bool TryGetHourly(string instanceType, out decimal price) => _perHour.TryGetValue(instanceType, out price);

    /// <summary>
    /// This table with prices from the AWS Price List API over it (issue #67); <paramref name="problem"/> says why
    /// live prices are missing, when they are.
    /// </summary>
    public PriceTable WithLive(IReadOnlyDictionary<string, decimal> live, string region, string? problem)
    {
        var merged = new Dictionary<string, decimal>(_perHour, StringComparer.OrdinalIgnoreCase);
        foreach (var (type, price) in live)
        {
            merged[type] = price;
        }

        var problems = string.Join(" ", new[] { Problem, problem }.Where(p => p is not null));
        var table = new PriceTable(merged, Basis, Source, problems.Length > 0 ? problems : null) { _liveRegion = region };
        table._live.UnionWith(live.Keys);
        return table;
    }

    /// <summary>The sentence for an estimate over these instance types: live prices, or the table's basis.</summary>
    public string StatementFor(IEnumerable<string> instanceTypes) =>
        _live.Count > 0 && instanceTypes.All(_live.Contains)
            ? $"Estimate from on-demand {_liveRegion} prices from the AWS Price List; managed spot and discounts differ."
            : Statement;

    /// <summary>
    /// Reads a price table: <c>{ "basis": "...", "perHour": { "ml.m5.large": 0.115, ... } }</c>.
    /// </summary>
    /// <exception cref="FormatException">The text is not such a table.</exception>
    public static PriceTable Parse(string json, string source, string? problem = null)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["perHour"] is not JsonObject perHour)
            {
                throw new FormatException("A price table needs a \"perHour\" object.");
            }

            var basis = root["basis"] is JsonValue value && value.TryGetValue(out string? text) && text.Length > 0 ? text : "a price table without a stated basis";
            var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var (type, price) in perHour)
            {
                prices[type] = price?.GetValue<decimal>() ?? throw new FormatException($"The price of {type} is missing.");
            }

            return new PriceTable(prices, basis, source, problem);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new FormatException(ex.Message, ex);
        }
    }

    /// <summary>The user's <c>prices.json</c> next to the plugin, else the built-in table.</summary>
    public static PriceTable Load() => Load(Path.GetDirectoryName(typeof(PriceTable).Assembly.Location));

    /// <summary>The <c>prices.json</c> in <paramref name="folder"/>, else the built-in table (with the reason when the file was unusable).</summary>
    public static PriceTable Load(string? folder)
    {
        string? problem = null;
        if (!string.IsNullOrEmpty(folder) && Path.Combine(folder, FileName) is var path && File.Exists(path))
        {
            try
            {
                return Parse(File.ReadAllText(path), path);
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                problem = $"{path} could not be read ({ex.Message}); the built-in prices are used.";
            }
        }

        return Parse(BuiltIn(), "built-in table", problem);
    }

    private static string BuiltIn()
    {
        using var stream = typeof(PriceTable).Assembly.GetManifestResourceStream("Habilis.Aws.SageMaker.prices.json")
            ?? throw new InvalidOperationException("The built-in price table is missing from the plugin assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>An estimated cost: the amount (null when it cannot be estimated) and how it was worked out.</summary>
internal sealed record CostEstimate(decimal? Amount, IReadOnlyList<string> Lines, string Statement)
{
    /// <summary>The panel cell: "$12.34", or "?" when there is no estimate.</summary>
    public string Text => Amount is { } amount ? JobCost.Money(amount) : "?";
}

/// <summary>Estimates a job's cost from on-demand prices: price per instance-hour × instances × billable hours, plus warm pool time.</summary>
internal static class JobCost
{
    public static CostEstimate Estimate(DescribeTrainingJobResponse job, PriceTable prices, DateTime now)
    {
        var lines = new List<string>();
        if (prices.Problem is { } problem)
        {
            lines.Add(problem);
        }

        var instances = JobFacts.Instances(job);
        if (instances.Count == 0)
        {
            lines.Add("No estimate: the job reports no instance type.");
            return new CostEstimate(null, lines, prices.Statement);
        }

        var statement = prices.StatementFor(instances.Select(i => i.Type));
        var unknown = instances.Select(i => i.Type).Where(t => !prices.TryGetHourly(t, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unknown.Count > 0)
        {
            lines.Add($"No estimate: neither the AWS Price List nor the price table has a price for {string.Join(", ", unknown)}. Add it to {PriceTable.FileName} next to the plugin to see an estimate.");
            return new CostEstimate(null, lines, statement);
        }

        var (billable, basis) = BillableTime(job, now);
        var retained = TimeSpan.FromSeconds(job.WarmPoolStatus?.ResourceRetainedBillableTimeInSeconds ?? 0);
        var total = 0m;
        foreach (var use in instances)
        {
            prices.TryGetHourly(use.Type, out var hourly);
            var cost = Cost(hourly, use.Count, billable);
            total += cost;
            lines.Add($"{use} at {Hourly(hourly)} × {AwsText.Duration(billable)} {basis} = {Money(cost)}");
            if (retained > TimeSpan.Zero)
            {
                var pool = Cost(hourly, use.Count, retained);
                total += pool;
                lines.Add($"{use} at {Hourly(hourly)} × {AwsText.Duration(retained)} kept in the warm pool = {Money(pool)}");
            }
        }

        if (job.EnableManagedSpotTraining == true)
        {
            lines.Add("Managed spot training is billed at spot prices, usually well below this on-demand estimate.");
        }

        if (JobFacts.IsRunning(job))
        {
            lines.Add("The job is still running: the cost grows until it ends.");
        }
        else if (JobFacts.WarmPoolLeft(job, now) is not null)
        {
            lines.Add("The warm pool is still available: its time is billed until it ends or is reused.");
        }

        return new CostEstimate(total, lines, statement);
    }

    /// <summary>
    /// The billable wall-clock time per instance: what SageMaker reports, else the time since training started for a
    /// running job, else the training time; with a few words saying which.
    /// </summary>
    public static (TimeSpan Time, string Basis) BillableTime(DescribeTrainingJobResponse job, DateTime now)
    {
        if (job.BillableTimeInSeconds is { } billable)
        {
            return (TimeSpan.FromSeconds(billable), "billable");
        }

        if (JobFacts.IsRunning(job))
        {
            return job.TrainingStartTime is { } started
                ? (Max(JobFacts.Utc(now) - JobFacts.Utc(started), TimeSpan.Zero), "since training started")
                : (TimeSpan.Zero, "(training has not started)");
        }

        if (job.TrainingTimeInSeconds is { } training)
        {
            return (TimeSpan.FromSeconds(training), "training time");
        }

        return job is { TrainingStartTime: { } start, TrainingEndTime: { } end }
            ? (Max(JobFacts.Utc(end) - JobFacts.Utc(start), TimeSpan.Zero), "from training start to end")
            : (TimeSpan.Zero, "(no billable time reported)");
    }

    public static string Money(decimal amount) => "$" + amount.ToString("N2", CultureInfo.InvariantCulture);

    private static string Hourly(decimal price) => "$" + price.ToString("0.00##", CultureInfo.InvariantCulture) + "/h";

    private static decimal Cost(decimal hourly, int count, TimeSpan time) => hourly * count * (decimal)time.TotalSeconds / 3600m;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
