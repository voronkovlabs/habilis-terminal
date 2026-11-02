using System.Runtime.CompilerServices;
using Amazon.SQS;
using Amazon.SQS.Model;
using Habilis.Aws.Common;

namespace Habilis.Aws.Sqs;

/// <summary>Message counts of a queue, as Amazon SQS reports them (approximate).</summary>
internal readonly record struct QueueCounts(long Available, long InFlight);

/// <summary>The SQS calls the plugin makes; a fake stands in for it in the tests.</summary>
internal interface ISqsApi : IDisposable
{
    /// <summary>Queue URLs whose names start with <paramref name="prefix"/>, page by page.</summary>
    IAsyncEnumerable<IReadOnlyList<string>> ListQueueUrlsAsync(string prefix, CancellationToken cancellationToken);

    Task<QueueCounts> GetCountsAsync(string queueUrl, CancellationToken cancellationToken);

    Task PurgeAsync(string queueUrl, CancellationToken cancellationToken);
}

/// <summary>The real SQS client for one profile and region.</summary>
internal sealed class SqsApi(AwsSession session) : ISqsApi
{
    private readonly AmazonSQSClient _client = new(session.Credentials, session.Configure(new AmazonSQSConfig()));

    public async IAsyncEnumerable<IReadOnlyList<string>> ListQueueUrlsAsync(string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? next = null;
        do
        {
            var request = new ListQueuesRequest { MaxResults = 1000, NextToken = next };
            if (prefix.Length > 0)
            {
                request.QueueNamePrefix = prefix;
            }

            var response = await _client.ListQueuesAsync(request, cancellationToken).ConfigureAwait(false);
            yield return response.QueueUrls ?? [];
            next = response.NextToken;
        }
        while (!string.IsNullOrEmpty(next));
    }

    public async Task<QueueCounts> GetCountsAsync(string queueUrl, CancellationToken cancellationToken)
    {
        var response = await _client.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = [QueueAttributeName.ApproximateNumberOfMessages, QueueAttributeName.ApproximateNumberOfMessagesNotVisible],
        }, cancellationToken).ConfigureAwait(false);
        return new QueueCounts(response.ApproximateNumberOfMessages, response.ApproximateNumberOfMessagesNotVisible);
    }

    public Task PurgeAsync(string queueUrl, CancellationToken cancellationToken) =>
        _client.PurgeQueueAsync(new PurgeQueueRequest { QueueUrl = queueUrl }, cancellationToken);

    public void Dispose() => _client.Dispose();
}
