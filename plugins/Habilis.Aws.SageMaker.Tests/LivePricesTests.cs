using Amazon.SageMaker.Model;
using Habilis.Aws.Common;
using Habilis.Aws.SageMaker;

namespace Habilis.Aws.SageMaker.Tests;

public sealed class LivePricesTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private static readonly PriceTable Table = PriceTable.Parse(
        """{ "basis": "test prices as of 2025-01", "perHour": { "ml.m5.xlarge": 0.23 } }""", "test");

    private static readonly AwsSession Session = new("prod", "eu-west-1");

    [Fact]
    public void The_hourly_price_comes_from_the_on_demand_terms_of_a_price_list_item()
    {
        const string item = """
            { "product": { "attributes": { "instanceName": "ml.g6.12xlarge", "component": "Training" } },
              "terms": { "OnDemand": { "SKU.JRTCKXETXF": { "priceDimensions": {
                "SKU.JRTCKXETXF.6YS6EN2CT7": { "unit": "Hrs", "pricePerUnit": { "USD": "5.7520000000" } } } } } } }
            """;

        Assert.Equal(5.752m, LivePrices.HourlyFrom(["not json", """{ "terms": {} }""", item]));
        Assert.Null(LivePrices.HourlyFrom([]));
    }

    [Fact]
    public async Task Live_prices_cover_types_the_table_lacks_and_are_asked_once()
    {
        var api = new FakePricing { ["ml.g6.12xlarge"] = 5.752m };
        var prices = new LivePrices(() => Table, _ => api, () => Now);

        var table = await prices.ForAsync(["ml.g6.12xlarge", "ml.m5.xlarge", "ml.g6.12xlarge"], Session, CancellationToken.None);
        await prices.ForAsync(["ml.g6.12xlarge"], Session, CancellationToken.None);

        Assert.True(table.TryGetHourly("ml.g6.12xlarge", out var g6));
        Assert.Equal(5.752m, g6);
        Assert.True(table.TryGetHourly("ml.m5.xlarge", out var m5)); // the API had none: the table answers
        Assert.Equal(0.23m, m5);
        Assert.Equal(["eu-west-1/ml.g6.12xlarge", "eu-west-1/ml.m5.xlarge"], api.Asked.Order(StringComparer.Ordinal));

        var cost = JobCost.Estimate(Job("ml.g6.12xlarge"), table, Now);
        Assert.Equal("$11.50", cost.Text); // 1 instance × 2 billable hours
        Assert.Contains("eu-west-1 prices from the AWS Price List", cost.Statement, StringComparison.Ordinal);
        Assert.Contains("test prices as of 2025-01", JobCost.Estimate(Job("ml.m5.xlarge"), table, Now).Statement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_price_list_falls_back_to_the_table_says_why_and_waits_before_asking_again()
    {
        var now = Now;
        var api = new FakePricing { Failure = new InvalidOperationException("no pricing:GetProducts permission") };
        var prices = new LivePrices(() => Table, _ => api, () => now);

        var table = await prices.ForAsync(["ml.g6.12xlarge"], Session, CancellationToken.None);
        var cost = JobCost.Estimate(Job("ml.g6.12xlarge"), table, Now);

        Assert.Null(cost.Amount);
        Assert.Contains(cost.Lines, line => line.Contains("Live prices are not available", StringComparison.Ordinal) && line.Contains("pricing:GetProducts", StringComparison.Ordinal));

        await prices.ForAsync(["ml.g6.12xlarge"], Session, CancellationToken.None);
        Assert.Single(api.Asked);

        now += TimeSpan.FromMinutes(31);
        api.Failure = null;
        api["ml.g6.12xlarge"] = 5.752m;
        table = await prices.ForAsync(["ml.g6.12xlarge"], Session, CancellationToken.None);
        Assert.True(table.TryGetHourly("ml.g6.12xlarge", out _));
        Assert.Null(table.Problem);
    }

    private static DescribeTrainingJobResponse Job(string instanceType) => new()
    {
        TrainingJobName = "job",
        TrainingJobStatus = Amazon.SageMaker.TrainingJobStatus.Completed,
        ResourceConfig = new ResourceConfig { InstanceType = instanceType, InstanceCount = 1 },
        BillableTimeInSeconds = 7200,
    };

    private sealed class FakePricing : IPricingApi
    {
        private readonly Dictionary<string, decimal> _prices = [];

        public List<string> Asked { get; } = [];

        public Exception? Failure { get; set; }

        public decimal this[string type]
        {
            set => _prices[type] = value;
        }

        public Task<decimal?> TrainingHourlyAsync(string instanceType, string region, CancellationToken cancellationToken)
        {
            lock (Asked)
            {
                Asked.Add($"{region}/{instanceType}");
            }

            return Failure is { } failure ? Task.FromException<decimal?>(failure) : Task.FromResult(_prices.TryGetValue(instanceType, out var price) ? price : (decimal?)null);
        }

        public void Dispose()
        {
        }
    }
}
