using System.Net;
using System.Runtime.CompilerServices;
using Amazon.SQS;
using Amazon.SQS.Model;
using Habilis.Aws.Common;
using Habilis.Aws.Sqs;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.Tests;

public class SqsTests
{
    private const string Base = "https://sqs.eu-west-1.amazonaws.com/123456789012/";

    [Fact]
    public async Task Lists_queues_by_prefix_with_counts_and_hides_excluded_ones()
    {
        var api = new FakeSqs(["orders", "orders-dlq", "orders-retry", "payments"]);
        var host = new FakeHost();
        new SqsSettings { NamePrefix = "orders", Exclusions = ["*-dlq"] }.Save(host.Settings);
        var panel = await OpenAsync(host, api, "sqs:prod/eu-west-1");

        var entries = await Listing.AllAsync(panel);

        Assert.Equal("orders", api.LastPrefix);
        Assert.Equal(["orders", "orders-retry"], entries.Select(e => e.Name));
        Assert.Equal(7L, entries[0].Cells![SqsPanel.AvailableKey].SortValue);
        Assert.Equal(1L, entries[0].Cells![SqsPanel.InFlightKey].SortValue);
        Assert.Equal("sqs:prod/eu-west-1", panel.Location);
        Assert.Contains(panel.Info, l => l is { Label: "Queues shown", Value: "2" });
        Assert.Contains(panel.Info, l => l is { Label: "Queues hidden", Value: "1" });
    }

    [Fact]
    public async Task Pages_come_as_separate_batches()
    {
        var api = new FakeSqs([.. Enumerable.Range(0, 5).Select(i => $"q{i}")]) { PageSize = 2 };
        var panel = await OpenAsync(new FakeHost(), api, "sqs:prod/eu-west-1");

        var batches = new List<int>();
        await foreach (var batch in panel.ListAsync(CancellationToken.None))
        {
            batches.Add(batch.Count);
        }

        Assert.Equal([2, 2, 1], batches);
    }

    [Fact]
    public async Task Purge_runs_only_after_confirming_the_named_queues_and_reports_each()
    {
        var api = new FakeSqs(["a", "b", "c"]) { FailPurge = "b" };
        var host = new FakeHost();
        host.ConfirmAnswers.Enqueue(true);
        var panel = await OpenAsync(host, api, "sqs:prod/eu-west-1");
        var entries = await Listing.AllAsync(panel);
        var selected = entries.Where(e => e.Name != "c").ToList();

        await Purge(panel).Run(new PanelCommandContext(selected[0], selected, new FakeProgress(), CancellationToken.None));

        var confirmation = Assert.Single(host.Confirmations);
        Assert.Equal(["a", "b"], confirmation.Items);
        Assert.Equal([Base + "a", Base + "b"], api.PurgeAttempts);
        var report = Assert.Single(host.Messages);
        Assert.Contains("a: purged", report.Message, StringComparison.Ordinal);
        Assert.Contains("b: not purged, a purge already ran", report.Message, StringComparison.Ordinal);
        Assert.Equal(MessageKind.Warning, report.Kind);
    }

    [Fact]
    public async Task Declining_the_confirmation_purges_nothing()
    {
        var api = new FakeSqs(["a"]);
        var host = new FakeHost();
        host.ConfirmAnswers.Enqueue(false);
        var panel = await OpenAsync(host, api, "sqs:prod/eu-west-1");
        var entries = await Listing.AllAsync(panel);

        await Purge(panel).Run(new PanelCommandContext(entries[0], entries, new FakeProgress(), CancellationToken.None));

        Assert.Empty(api.PurgeAttempts);
        Assert.Empty(host.Messages);
    }

    [Fact]
    public async Task Switching_profile_and_region_changes_the_location()
    {
        var host = new FakeHost();
        host.ChooseAnswers.Enqueue("staging");
        host.ChooseAnswers.Enqueue("us-west-2");
        var panel = await OpenAsync(host, new FakeSqs(["a"]), "sqs:prod/eu-west-1");

        await panel.Commands.Single(c => c.Key == "Shift+F2").Run(new PanelCommandContext(null, [], new FakeProgress(), CancellationToken.None));

        Assert.Equal("sqs:staging/us-west-2", panel.Location);
    }

    [Fact]
    public async Task Access_denied_says_which_permission_is_missing()
    {
        var api = new FakeSqs(["a"])
        {
            ListError = new AmazonSQSException("User: arn:aws:iam::1:user/x is not authorized to perform: sqs:ListQueues on resource") { ErrorCode = "AccessDenied", StatusCode = HttpStatusCode.Forbidden },
        };
        var panel = await OpenAsync(new FakeHost(), api, "sqs:prod/eu-west-1");

        var error = await Assert.ThrowsAsync<PluginException>(() => Listing.AllAsync(panel));

        Assert.Contains("Permission missing: sqs:ListQueues", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("arn:aws", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sqs:", null, null)]
    [InlineData("sqs:prod", "prod", null)]
    [InlineData("sqs:prod/eu-west-1", "prod", "eu-west-1")]
    [InlineData("SQS: prod / eu-west-1 ", "prod", "eu-west-1")]
    public void Addresses_name_profile_and_region(string address, string? profile, string? region) =>
        Assert.Equal((profile, region), AwsText.ParseProfileAddress(address));

    [Theory]
    [InlineData("orders-dlq", "*-dlq", true)]
    [InlineData("orders-DLQ", "*-dlq", true)]
    [InlineData("orders", "*-dlq", false)]
    [InlineData("test-1", "test-?", true)]
    [InlineData("a.b", "a?b", true)]
    [InlineData("aXb", "a.b", false)]
    public void Wildcards_match_whole_names(string name, string pattern, bool expected) =>
        Assert.Equal(expected, AwsText.MatchesWildcard(name, pattern));

    private static PanelCommand Purge(IPanelCommands panel) => panel.Commands.Single(c => c.Key == "F8");

    private static async Task<SqsPanel> OpenAsync(FakeHost host, FakeSqs api, string address)
    {
        var plugin = new SqsPlugin { Connect = _ => api };
        plugin.Initialize(host);
        return (SqsPanel)(await plugin.OpenAddressAsync(address, CancellationToken.None))!;
    }

    private sealed class FakeSqs(IReadOnlyList<string> names) : ISqsApi
    {
        public int PageSize { get; init; } = 1000;

        public string? FailPurge { get; init; }

        public Exception? ListError { get; init; }

        public string? LastPrefix { get; private set; }

        public List<string> PurgeAttempts { get; } = [];

        public async IAsyncEnumerable<IReadOnlyList<string>> ListQueueUrlsAsync(string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastPrefix = prefix;
            await Task.Yield();
            if (ListError is not null)
            {
                throw ListError;
            }

            var matching = names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Select(n => Base + n).ToList();
            for (var i = 0; i < matching.Count; i += PageSize)
            {
                yield return matching.Skip(i).Take(PageSize).ToList();
            }
        }

        public Task<QueueCounts> GetCountsAsync(string queueUrl, CancellationToken cancellationToken) =>
            Task.FromResult(new QueueCounts(SqsPanel.NameOf(queueUrl).Length + 1, 1));

        public Task PurgeAsync(string queueUrl, CancellationToken cancellationToken)
        {
            lock (PurgeAttempts)
            {
                PurgeAttempts.Add(queueUrl);
            }

            return SqsPanel.NameOf(queueUrl) == FailPurge
                ? Task.FromException(new PurgeQueueInProgressException("Only one PurgeQueue operation is allowed every 60 seconds."))
                : Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
