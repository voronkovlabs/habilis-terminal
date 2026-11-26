using System.Globalization;
using System.Runtime.CompilerServices;
using Amazon.SQS.Model;
using Habilis.Aws.Sqs;

namespace Habilis.Aws.Tests;

/// <summary>
/// Amazon SQS in memory: queues by name, messages with visibility and receive counts as SQS keeps them, so the panel's
/// receiving, holding, releasing, deleting and sending can be checked without a network (issue #397).
/// </summary>
internal sealed class FakeSqs(IReadOnlyList<string> names) : ISqsApi
{
    public const string Base = "https://sqs.eu-west-1.amazonaws.com/123456789012/";

    private readonly object _gate = new();
    private int _next;

    public int PageSize { get; init; } = 1000;

    public string? FailPurge { get; init; }

    public Exception? ListError { get; init; }

    public string? LastPrefix { get; private set; }

    public List<string> PurgeAttempts { get; } = [];

    public Dictionary<string, QueueInfo> Infos { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<Stored>> Queues { get; } = new(StringComparer.Ordinal);

    /// <summary>(queue, max, wait, visibility) of each ReceiveMessage.</summary>
    public List<(string Queue, int Max, int Wait, int Visibility)> Receives { get; } = [];

    public List<(string Queue, string Id, int Seconds)> VisibilityChanges { get; } = [];

    public List<(string Queue, SqsOutgoing Message)> Sent { get; } = [];

    public static string Url(string name) => Base + name;

    /// <summary>Puts a message in a queue as a producer would.</summary>
    public Stored Add(string queue, string body, string? group = null)
    {
        lock (_gate)
        {
            var message = new Stored($"m{++_next:000}", body, group, DateTime.UtcNow.AddMinutes(-_next));
            if (!Queues.TryGetValue(queue, out var list))
            {
                Queues[queue] = list = [];
            }

            list.Add(message);
            return message;
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<string>> ListQueueUrlsAsync(string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LastPrefix = prefix;
        await Task.Yield();
        if (ListError is not null)
        {
            throw ListError;
        }

        var matching = names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Select(Url).ToList();
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

    public Task<QueueInfo> GetInfoAsync(string queueUrl, CancellationToken cancellationToken)
    {
        var name = SqsPanel.NameOf(queueUrl);
        return Task.FromResult(Infos.TryGetValue(name, out var info) ? info : new QueueInfo(name.EndsWith(".fifo", StringComparison.Ordinal), 30));
    }

    public Task<IReadOnlyList<SqsMessage>> ReceiveAsync(string queueUrl, int maxMessages, int waitSeconds, int visibilitySeconds, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var name = SqsPanel.NameOf(queueUrl);
            Receives.Add((name, maxMessages, waitSeconds, visibilitySeconds));
            var now = DateTime.UtcNow;
            var taken = Queues.GetValueOrDefault(name, []).Where(m => m.HiddenUntil <= now).Take(maxMessages).ToList();
            foreach (var message in taken)
            {
                message.ReceiveCount++;
                message.Handle = $"{message.Id}-r{message.ReceiveCount}";
                message.HiddenUntil = now.AddSeconds(visibilitySeconds);
            }

            return Task.FromResult<IReadOnlyList<SqsMessage>>(taken.Select(m => m.ToMessage()).ToList());
        }
    }

    public Task<IReadOnlyDictionary<string, string>> DeleteAsync(string queueUrl, IReadOnlyList<SqsMessage> messages, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var list = Queues.GetValueOrDefault(SqsPanel.NameOf(queueUrl), []);
            var failed = new Dictionary<string, string>();
            foreach (var message in messages)
            {
                var stored = list.FirstOrDefault(m => m.Handle == message.ReceiptHandle);
                if (stored is null)
                {
                    failed[message.Id] = "The receipt handle has expired.";
                }
                else
                {
                    list.Remove(stored);
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, string>>(failed);
        }
    }

    public Task ChangeVisibilityAsync(string queueUrl, IReadOnlyList<SqsMessage> messages, int seconds, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var name = SqsPanel.NameOf(queueUrl);
            foreach (var message in messages)
            {
                VisibilityChanges.Add((name, message.Id, seconds));
                if (Queues.GetValueOrDefault(name, []).FirstOrDefault(m => m.Handle == message.ReceiptHandle) is { } stored)
                {
                    stored.HiddenUntil = DateTime.UtcNow.AddSeconds(seconds);
                }
            }

            return Task.CompletedTask;
        }
    }

    public Task<string> SendAsync(string queueUrl, SqsOutgoing message, CancellationToken cancellationToken)
    {
        var name = SqsPanel.NameOf(queueUrl);
        lock (_gate)
        {
            Sent.Add((name, message));
        }

        return Task.FromResult(Add(name, message.Body, message.GroupId).Id);
    }

    public void Dispose()
    {
    }

    /// <summary>A message as the queue keeps it.</summary>
    internal sealed class Stored(string id, string body, string? group, DateTime sent)
    {
        public string Id { get; } = id;

        public string Body { get; } = body;

        public string? Group { get; } = group;

        public DateTime Sent { get; } = sent;

        public int ReceiveCount { get; set; }

        public string Handle { get; set; } = string.Empty;

        public DateTime HiddenUntil { get; set; } = DateTime.MinValue;

        public bool Visible => HiddenUntil <= DateTime.UtcNow;

        public SqsMessage ToMessage()
        {
            var system = new Dictionary<string, string>
            {
                ["ApproximateReceiveCount"] = ReceiveCount.ToString(CultureInfo.InvariantCulture),
                ["SentTimestamp"] = new DateTimeOffset(Sent).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                ["SenderId"] = "AIDAEXAMPLE",
            };
            if (Group is not null)
            {
                system["MessageGroupId"] = Group;
            }

            return new SqsMessage(Id, Handle, Body, system, new Dictionary<string, SqsAttribute> { ["source"] = new("String", "test") }, "md5");
        }
    }
}
