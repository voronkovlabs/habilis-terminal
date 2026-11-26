using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Habilis.Aws.Common;

namespace Habilis.Aws.Sqs;

/// <summary>Message counts of a queue, as Amazon SQS reports them (approximate).</summary>
internal readonly record struct QueueCounts(long Available, long InFlight);

/// <summary>What browsing and sending need to know about a queue (issue #397).</summary>
/// <param name="DeadLetterQueue">The dead-letter queue's name when a redrive policy is set.</param>
/// <param name="MaxReceiveCount">Receives after which Amazon SQS moves a message to the dead-letter queue.</param>
/// <param name="ContentDeduplication">A FIFO queue deduplicates by a hash of the body when no deduplication ID is sent.</param>
internal sealed record QueueInfo(bool Fifo, int VisibilityTimeout, string? DeadLetterQueue = null, int? MaxReceiveCount = null, bool ContentDeduplication = false);

/// <summary>A message attribute: its data type (String, Number, Binary, or a custom "String.x") and value (Base64 for Binary).</summary>
internal sealed record SqsAttribute(string DataType, string Value);

/// <summary>A received message (issue #397).</summary>
internal sealed record SqsMessage(
    string Id,
    string ReceiptHandle,
    string Body,
    IReadOnlyDictionary<string, string> SystemAttributes,
    IReadOnlyDictionary<string, SqsAttribute> Attributes,
    string? BodyMd5 = null);

/// <summary>A message to send (issue #397).</summary>
internal sealed record SqsOutgoing(
    string Body,
    string? GroupId = null,
    string? DeduplicationId = null,
    int DelaySeconds = 0,
    IReadOnlyDictionary<string, SqsAttribute>? Attributes = null);

/// <summary>The SQS calls the plugin makes; a fake stands in for it in the tests.</summary>
internal interface ISqsApi : IDisposable
{
    /// <summary>Queue URLs whose names start with <paramref name="prefix"/>, page by page.</summary>
    IAsyncEnumerable<IReadOnlyList<string>> ListQueueUrlsAsync(string prefix, CancellationToken cancellationToken);

    Task<QueueCounts> GetCountsAsync(string queueUrl, CancellationToken cancellationToken);

    Task PurgeAsync(string queueUrl, CancellationToken cancellationToken);

    Task<QueueInfo> GetInfoAsync(string queueUrl, CancellationToken cancellationToken);

    /// <summary>Up to 10 messages, with all system and message attributes.</summary>
    Task<IReadOnlyList<SqsMessage>> ReceiveAsync(string queueUrl, int maxMessages, int waitSeconds, int visibilitySeconds, CancellationToken cancellationToken);

    /// <summary>Deletes by receipt handle, 10 per request. Returns the messages not deleted with the reason, by message ID.</summary>
    Task<IReadOnlyDictionary<string, string>> DeleteAsync(string queueUrl, IReadOnlyList<SqsMessage> messages, CancellationToken cancellationToken);

    /// <summary>Changes how long the messages stay hidden; 0 makes them visible to consumers again.</summary>
    Task ChangeVisibilityAsync(string queueUrl, IReadOnlyList<SqsMessage> messages, int seconds, CancellationToken cancellationToken);

    /// <summary>Sends one message; returns its message ID.</summary>
    Task<string> SendAsync(string queueUrl, SqsOutgoing message, CancellationToken cancellationToken);
}

/// <summary>The real SQS client for one profile and region.</summary>
internal sealed class SqsApi(AwsSession session) : ISqsApi
{
    private const int BatchSize = 10;
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

    public async Task<QueueInfo> GetInfoAsync(string queueUrl, CancellationToken cancellationToken)
    {
        var response = await _client.GetQueueAttributesAsync(InfoRequest(queueUrl), cancellationToken).ConfigureAwait(false);
        var attributes = response.Attributes ?? [];
        var (deadLetter, maxReceives) = attributes.TryGetValue("RedrivePolicy", out var policy) ? ParseRedrive(policy) : (null, null);
        var fifo = attributes.TryGetValue("FifoQueue", out var f)
            ? string.Equals(f, "true", StringComparison.OrdinalIgnoreCase)
            : SqsPanel.NameOf(queueUrl).EndsWith(".fifo", StringComparison.Ordinal);
        var visibility = attributes.TryGetValue("VisibilityTimeout", out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ? seconds : 30;
        var contentDeduplication = attributes.TryGetValue("ContentBasedDeduplication", out var c) && string.Equals(c, "true", StringComparison.OrdinalIgnoreCase);
        return new QueueInfo(fifo, visibility, deadLetter, maxReceives, contentDeduplication);
    }

    public async Task<IReadOnlyList<SqsMessage>> ReceiveAsync(string queueUrl, int maxMessages, int waitSeconds, int visibilitySeconds, CancellationToken cancellationToken)
    {
        var response = await _client.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = Math.Clamp(maxMessages, 1, BatchSize),
            WaitTimeSeconds = Math.Clamp(waitSeconds, 0, 20),
            VisibilityTimeout = visibilitySeconds,
            MessageSystemAttributeNames = ["All"],
            MessageAttributeNames = ["All"],
        }, cancellationToken).ConfigureAwait(false);
        return (response.Messages ?? []).Select(m => new SqsMessage(
            m.MessageId,
            m.ReceiptHandle,
            m.Body ?? string.Empty,
            m.Attributes ?? [],
            (m.MessageAttributes ?? []).ToDictionary(a => a.Key, a => ToAttribute(a.Value)),
            m.MD5OfBody)).ToList();
    }

    public async Task<IReadOnlyDictionary<string, string>> DeleteAsync(string queueUrl, IReadOnlyList<SqsMessage> messages, CancellationToken cancellationToken)
    {
        var failed = new Dictionary<string, string>();
        foreach (var chunk in messages.Chunk(BatchSize))
        {
            var response = await _client.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
            {
                QueueUrl = queueUrl,
                Entries = chunk.Select((m, i) => new DeleteMessageBatchRequestEntry(i.ToString(CultureInfo.InvariantCulture), m.ReceiptHandle)).ToList(),
            }, cancellationToken).ConfigureAwait(false);
            foreach (var error in response.Failed ?? [])
            {
                failed[chunk[int.Parse(error.Id, CultureInfo.InvariantCulture)].Id] = error.Message ?? error.Code;
            }
        }

        return failed;
    }

    public async Task ChangeVisibilityAsync(string queueUrl, IReadOnlyList<SqsMessage> messages, int seconds, CancellationToken cancellationToken)
    {
        foreach (var chunk in messages.Chunk(BatchSize))
        {
            await _client.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
            {
                QueueUrl = queueUrl,
                Entries = chunk.Select((m, i) => new ChangeMessageVisibilityBatchRequestEntry(i.ToString(CultureInfo.InvariantCulture), m.ReceiptHandle) { VisibilityTimeout = seconds }).ToList(),
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string> SendAsync(string queueUrl, SqsOutgoing message, CancellationToken cancellationToken)
    {
        var request = new SendMessageRequest { QueueUrl = queueUrl, MessageBody = message.Body };
        if (message.GroupId is { Length: > 0 } group)
        {
            request.MessageGroupId = group;
        }

        if (message.DeduplicationId is { Length: > 0 } deduplication)
        {
            request.MessageDeduplicationId = deduplication;
        }

        if (message.DelaySeconds > 0)
        {
            request.DelaySeconds = message.DelaySeconds;
        }

        if (message.Attributes is { Count: > 0 } attributes)
        {
            request.MessageAttributes = attributes.ToDictionary(a => a.Key, a => a.Value.DataType.StartsWith("Binary", StringComparison.Ordinal)
                ? new MessageAttributeValue { DataType = a.Value.DataType, BinaryValue = new MemoryStream(Convert.FromBase64String(a.Value.Value)) }
                : new MessageAttributeValue { DataType = a.Value.DataType, StringValue = a.Value.Value });
        }

        var response = await _client.SendMessageAsync(request, cancellationToken).ConfigureAwait(false);
        return response.MessageId;
    }

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// All attributes: a standard queue rejects FifoQueue and ContentBasedDeduplication by name
    /// ("Unknown Attribute FifoQueue", issue #400), and a missing one then means the default.
    /// </summary>
    internal static GetQueueAttributesRequest InfoRequest(string queueUrl) => new()
    {
        QueueUrl = queueUrl,
        AttributeNames = [QueueAttributeName.All],
    };

    /// <summary>The dead-letter queue's name and the receive limit from a RedrivePolicy (numbers may come as text).</summary>
    internal static (string? Queue, int? MaxReceiveCount) ParseRedrive(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            return (null, null);
        }

        try
        {
            using var json = JsonDocument.Parse(policy);
            var root = json.RootElement;
            var arn = root.TryGetProperty("deadLetterTargetArn", out var a) ? a.GetString() : null;
            int? max = null;
            if (root.TryGetProperty("maxReceiveCount", out var m))
            {
                if (m.ValueKind == JsonValueKind.Number)
                {
                    max = m.GetInt32();
                }
                else if (int.TryParse(m.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    max = n;
                }
            }

            return (arn is null ? null : arn[(arn.LastIndexOf(':') + 1)..], max);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static SqsAttribute ToAttribute(MessageAttributeValue value) =>
        value.BinaryValue is { } binary && value.StringValue is null
            ? new SqsAttribute(value.DataType, Convert.ToBase64String(binary.ToArray()))
            : new SqsAttribute(value.DataType, value.StringValue ?? string.Empty);
}
