using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Habilis.Aws.Common;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.Sqs;

/// <summary>
/// The messages of one queue (issue #397), as the AWS console's "Send and receive messages" page has them. Enter on a
/// queue receives up to <see cref="SqsSettings.ReceiveLimit"/> messages and keeps them hidden from consumers for
/// <see cref="SqsSettings.VisibilitySeconds"/>; a refresh keeps the ones still held and receives new ones, and leaving
/// the queue makes the rest visible again. F3 shows a message, F8 deletes, F5 saves bodies to files, F5 of files into the
/// queue sends them, Shift+F4 sends a message from a form.
/// </summary>
internal sealed partial class SqsPanel
{
    public const string ReceivesKey = "receives";
    public const string SentKey = "sent";
    public const string GroupKey = "group";
    public const string BodyKey = "body";

    /// <summary>Bodies above this are not read from files: Amazon SQS takes at most 1 MiB.</summary>
    internal const int MaxFileBytes = 1024 * 1024;

    private const int QuickViewChars = 64 * 1024;

    private static readonly PanelLayout[] MessageLayouts =
    [
        new("Messages",
        [
            PanelColumn.Size,
            new PanelColumn(SentKey, "Sent", 140, AlignRight: true),
            new PanelColumn(ReceivesKey, "Receives", 70, AlignRight: true),
            new PanelColumn(BodyKey, "Body", 420),
        ]),
        new("FIFO",
        [
            new PanelColumn(SentKey, "Sent", 140, AlignRight: true),
            new PanelColumn(GroupKey, "Group", 140),
            new PanelColumn(ReceivesKey, "Receives", 70, AlignRight: true),
            new PanelColumn(BodyKey, "Body", 360),
        ]),
        new("Names", []),
    ];

    private static readonly PanelSortKey[] MessageSortKeys = [new(SentKey, "Sent"), new(ReceivesKey, "Receive count"), new(GroupKey, "Message group")];

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly object _gate = new();

    /// <summary>Received messages by ID, in the order they came, with when they become visible to consumers again.</summary>
    private readonly Dictionary<string, Held> _held = new(StringComparer.Ordinal);

    private OpenQueue? _queue;

    /// <summary>Enter on a queue opens it; on a message it views it (the host does, on false).</summary>
    public async Task<bool> EnterAsync(PanelEntry entry, CancellationToken cancellationToken)
    {
        if (_queue is not null || entry.Tag is not string url)
        {
            return false;
        }

        QueueInfo info;
        try
        {
            info = await Api.GetInfoAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw AwsErrors.Describe(ex, $"Reading the settings of {entry.Name}", _session);
        }

        if (info.MaxReceiveCount is { } max && !IsWarned(url))
        {
            var hidden = _settings.VisibilitySeconds > 0
                ? $"While the queue is open, received messages stay hidden from consumers for {_settings.VisibilitySeconds} s; leaving the queue makes them visible again."
                : "Every refresh receives the messages again.";
            var open = await _host.Dialogs.ConfirmAsync(
                "Open queue",
                $"Opening {entry.Name} receives its messages, as the AWS console does: each receive adds 1 to a message's receive count, and after {max} receives Amazon SQS moves a message to {info.DeadLetterQueue ?? "the dead-letter queue"}. {hidden}",
                confirmText: "Open").ConfigureAwait(false);
            if (!open)
            {
                return false;
            }

            lock (_warned)
            {
                _warned.Add(url);
            }
        }

        lock (_gate)
        {
            _held.Clear();
        }

        _queue = new OpenQueue(url, entry.Name, info);
        return true;
    }

    /// <summary>".." in a queue: the held messages become visible again, and the queue list returns.</summary>
    public async Task<bool> GoUpAsync(CancellationToken cancellationToken)
    {
        if (_queue is null)
        {
            return false;
        }

        await ReleaseAsync().ConfigureAwait(false);
        _queue = null;
        return true;
    }

    /// <summary>Ctrl+Shift+C on a message copies its ID; on a queue, its URL.</summary>
    public string? EntryAddress(PanelEntry entry) => entry.Tag switch
    {
        string url => url,
        SqsMessage message => message.Id,
        _ => null,
    };

    /// <summary>
    /// The messages still held, then newly received ones, 10 per request, until the limit or a request brings nothing
    /// new. The first request waits <see cref="SqsSettings.WaitSeconds"/>; later ones 1 s, which asks every server.
    /// </summary>
    private async IAsyncEnumerable<IReadOnlyList<PanelEntry>> ListMessagesAsync(OpenQueue queue, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = _settings;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<SqsMessage> kept;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            foreach (var id in _held.Where(h => h.Value.HiddenUntil <= now.AddSeconds(1)).Select(h => h.Key).ToList())
            {
                _held.Remove(id);
            }

            kept = _held.Values.Select(h => h.Message).ToList();
        }

        seen.UnionWith(kept.Select(m => m.Id));
        if (kept.Count > 0)
        {
            yield return kept.Select(MessageEntry).ToList();
        }

        var remaining = settings.ReceiveLimit - kept.Count;
        var first = true;
        for (var round = 0; remaining > 0 && round < (settings.ReceiveLimit / 10) + 10; round++)
        {
            IReadOnlyList<SqsMessage> batch;
            try
            {
                batch = await Api.ReceiveAsync(queue.Url, Math.Min(10, remaining), first ? settings.WaitSeconds : 1, settings.VisibilitySeconds, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw AwsErrors.Describe(ex, $"Receiving messages from {queue.Name}", _session);
            }

            first = false;
            var hiddenUntil = DateTime.UtcNow.AddSeconds(settings.VisibilitySeconds);
            var fresh = new List<SqsMessage>();
            lock (_gate)
            {
                foreach (var message in batch)
                {
                    // A message received again (visibility 0) keeps its newest receipt handle, which deletes it.
                    _held[message.Id] = new Held(message, hiddenUntil);
                    if (seen.Add(message.Id))
                    {
                        fresh.Add(message);
                    }
                }
            }

            if (fresh.Count == 0)
            {
                break;
            }

            remaining -= fresh.Count;
            yield return fresh.Select(MessageEntry).ToList();
        }
    }

    /// <summary>F8 in a queue: deletes the messages (the host has confirmed, naming them).</summary>
    public async Task<OperationOutcome> DeleteAsync(IReadOnlyList<PanelEntry> entries, IOperationProgress progress, CancellationToken cancellationToken)
    {
        var messages = entries.Select(e => e.Tag).OfType<SqsMessage>().ToList();
        if (_queue is not { } queue || messages.Count == 0)
        {
            return new OperationOutcome(0, 0, entries.Count, "Purge queues with F8 in the queue list; delete messages inside a queue.");
        }

        progress.SetTotals(messages.Count, null);
        var failed = await DeleteMessagesAsync(queue, messages, cancellationToken).ConfigureAwait(false);
        return Outcome(messages.Count, failed, "deleted");
    }

    public bool CanMove => true;

    /// <summary>
    /// F5 from a queue: each message's body as a file named by its ID, .json when the body is JSON, else .txt, so F5 of
    /// the files into a queue sends the same bodies. F6 deletes the messages saved.
    /// </summary>
    public async Task<OperationOutcome> GetFilesAsync(TransferRequest request, IOperationProgress progress, CancellationToken cancellationToken)
    {
        var messages = request.Entries.Select(e => e.Tag).OfType<SqsMessage>().ToList();
        if (_queue is not { } queue || messages.Count == 0)
        {
            return new OperationOutcome(0, 0, request.Entries.Count, "Open a queue with Enter to save its messages.");
        }

        progress.SetTotals(messages.Count, messages.Sum(m => (long)Encoding.UTF8.GetByteCount(m.Body)));
        var saved = new List<SqsMessage>();
        var skipped = 0;
        var failed = 0;
        OverwriteChoice? always = null;
        var folder = Path.GetFullPath(request.LocalFolder);
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = FileNameOf(message);
            var target = Path.GetFullPath(Path.Combine(folder, name));
            if (!target.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                failed++;
                continue;
            }

            progress.Begin(name);
            if (File.Exists(target))
            {
                var choice = always ?? await request.AskOverwrite(target).ConfigureAwait(false);
                if (choice == OverwriteChoice.Cancel)
                {
                    break;
                }

                if (choice is OverwriteChoice.OverwriteAll or OverwriteChoice.SkipAll)
                {
                    always = choice;
                }

                if (choice is OverwriteChoice.Skip or OverwriteChoice.SkipAll)
                {
                    skipped++;
                    progress.CompleteItem();
                    continue;
                }
            }

            var bytes = Encoding.UTF8.GetBytes(message.Body);
            var temporary = target + ".habi-part";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, target, overwrite: true);
                saved.Add(message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
            finally
            {
                File.Delete(temporary);
            }

            progress.AddBytes(bytes.Length);
            progress.CompleteItem();
        }

        if (!request.Move || saved.Count == 0)
        {
            return new OperationOutcome(saved.Count, skipped, failed);
        }

        var notDeleted = await DeleteMessagesAsync(queue, saved, cancellationToken).ConfigureAwait(false);
        return new OperationOutcome(saved.Count - notDeleted.Count, skipped, failed + notDeleted.Count, notDeleted.Count > 0 ? $"Saved but not deleted: {Reasons(notDeleted)}" : null);
    }

    /// <summary>F5 of local files into a queue: each file's text is sent as one message (folders: the files in them).</summary>
    public async Task<OperationOutcome> PutFilesAsync(IReadOnlyList<string> localPaths, Func<string, Task<OverwriteChoice>> askOverwrite, IOperationProgress progress, CancellationToken cancellationToken)
    {
        if (_queue is not { } queue)
        {
            throw new PluginException("Open a queue with Enter, then copy files into it: each file is sent as one message.");
        }

        var files = localPaths.SelectMany<string, string>(path => Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)
            : [path]).ToList();
        string? group = null;
        if (queue.Info.Fifo)
        {
            group = await _host.Dialogs.InputAsync("Send files", $"Message group ID for the {files.Count} messages to the FIFO queue {queue.Name}", "habi").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(group))
            {
                return new OperationOutcome(0, files.Count, 0, "Nothing sent.");
            }
        }

        progress.SetTotals(files.Count, files.Sum(f => new FileInfo(f).Length));
        var sent = 0;
        var problems = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            progress.Begin(name);
            var length = new FileInfo(file).Length;
            if (length > MaxFileBytes)
            {
                problems.Add($"{name}: larger than 1 MiB, the most Amazon SQS takes");
                progress.CompleteItem();
                continue;
            }

            try
            {
                var body = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var outgoing = new SqsOutgoing(body, group, queue.Info.Fifo && !queue.Info.ContentDeduplication ? Guid.NewGuid().ToString("N") : null);
                await Api.SendAsync(queue.Url, outgoing, cancellationToken).ConfigureAwait(false);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add($"{name}: {(ex is IOException or UnauthorizedAccessException ? ex.Message : AwsErrors.Describe(ex, "Sending", _session).Message)}");
            }

            progress.AddBytes(length);
            progress.CompleteItem();
        }

        return new OperationOutcome(sent, 0, problems.Count, problems.Count > 0 ? string.Join(Environment.NewLine, problems) : null);
    }

    /// <summary>Shift+F4: a message from a form, to the queue under the cursor or the open queue.</summary>
    private async Task SendAsync(PanelCommandContext context)
    {
        string url;
        string name;
        QueueInfo info;
        if (_queue is { } open)
        {
            (url, name, info) = (open.Url, open.Name, open.Info);
        }
        else if (context.Current is { Tag: string current } entry)
        {
            (url, name) = (current, entry.Name);
            try
            {
                info = await Api.GetInfoAsync(url, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!context.CancellationToken.IsCancellationRequested)
            {
                throw AwsErrors.Describe(ex, $"Reading the settings of {name}", _session);
            }
        }
        else
        {
            return;
        }

        var form = SendForm(name, info, _lastSend);
        if (!await _host.Dialogs.EditAsync(form).ConfigureAwait(false))
        {
            return;
        }

        var outgoing = FromSendForm(form, info);
        _lastSend = outgoing with { DeduplicationId = null };
        string id;
        try
        {
            id = await Api.SendAsync(url, outgoing, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!context.CancellationToken.IsCancellationRequested)
        {
            throw AwsErrors.Describe(ex, $"Sending a message to {name}", _session);
        }

        _host.ShowStatus($"Sent to {name}: message {id}");
    }

    private SqsOutgoing? _lastSend;

    /// <summary>The send form: body, group and deduplication IDs (FIFO), delay (standard queues), attributes.</summary>
    internal static PluginForm SendForm(string queueName, QueueInfo info, SqsOutgoing? last)
    {
        var fields = new List<FormField>
        {
            new TextField("body", "Message body", last?.Body ?? string.Empty) { Lines = 10, Description = "Sent exactly as typed." },
            new TextField("group", info.Fifo ? "Message group ID" : "Message group ID (optional)", last?.GroupId ?? string.Empty)
            {
                Description = info.Fifo
                    ? "Required: messages of one group are delivered in order."
                    : "Optional: groups messages for fair processing across groups in a standard queue.",
            },
        };
        if (info.Fifo)
        {
            fields.Add(new TextField("deduplication", "Deduplication ID (optional)")
            {
                Description = info.ContentDeduplication
                    ? "Blank: the queue deduplicates by the body."
                    : "Blank: a new ID, so the message is never taken for a duplicate.",
            });
        }
        else
        {
            fields.Add(new NumberField("delay", "Delivery delay, seconds", last?.DelaySeconds ?? 0, 0, 900) { Description = "0 to 900: the message stays hidden from consumers this long." });
        }

        fields.Add(new ListField("attributes", "Message attributes (optional)", last?.Attributes?.Select(a => FormatAttribute(a.Key, a.Value)).ToList() ?? [])
        {
            Description = "One per line: Name=value (a String), Name:Number=42, Name:Binary=<Base64>, or a custom type such as Name:String.id=value.",
        });
        return new PluginForm($"Send a message to {queueName}", fields)
        {
            Validate = form =>
            {
                if (form.Field<TextField>("body").Value.Length == 0)
                {
                    return "The message body is empty.";
                }

                if (info.Fifo && form.Field<TextField>("group").Value.Trim().Length == 0)
                {
                    return "A FIFO queue needs a message group ID.";
                }

                return ParseAttributes(form.Field<ListField>("attributes").Values, out _);
            },
        };
    }

    internal static SqsOutgoing FromSendForm(PluginForm form, QueueInfo info)
    {
        ParseAttributes(form.Field<ListField>("attributes").Values, out var attributes);
        var group = form.Field<TextField>("group").Value.Trim();
        string? deduplication = null;
        if (info.Fifo)
        {
            deduplication = form.Field<TextField>("deduplication").Value.Trim();
            if (deduplication.Length == 0)
            {
                deduplication = info.ContentDeduplication ? null : Guid.NewGuid().ToString("N");
            }
        }

        return new SqsOutgoing(
            form.Field<TextField>("body").Value,
            group.Length > 0 ? group : null,
            deduplication,
            info.Fifo ? 0 : form.Field<NumberField>("delay").Value,
            attributes.Count > 0 ? attributes : null);
    }

    /// <summary>"Name=value", "Name:Type=value". Null when every line parses, else the error to show.</summary>
    internal static string? ParseAttributes(IReadOnlyList<string> lines, out Dictionary<string, SqsAttribute> attributes)
    {
        attributes = new Dictionary<string, SqsAttribute>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                return $"\"{line}\" is not Name=value.";
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            var colon = key.IndexOf(':', StringComparison.Ordinal);
            var name = colon >= 0 ? key[..colon].Trim() : key;
            var type = colon >= 0 ? key[(colon + 1)..].Trim() : "String";
            if (name.Length == 0 || name.Length > 256 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            {
                return $"\"{name}\" is not a valid attribute name: letters, digits, _, - and . only.";
            }

            var baseType = type.Split('.', 2)[0];
            if (baseType is not ("String" or "Number" or "Binary"))
            {
                return $"The type of {name} must be String, Number or Binary, optionally with .label.";
            }

            if (baseType == "Number" && !decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                return $"The value of {name} is not a number.";
            }

            if (baseType == "Binary")
            {
                try
                {
                    _ = Convert.FromBase64String(value);
                }
                catch (FormatException)
                {
                    return $"The value of {name} is not Base64.";
                }
            }

            if (!attributes.TryAdd(name, new SqsAttribute(type, value)))
            {
                return $"The attribute {name} appears twice.";
            }
        }

        return attributes.Count > 10 ? "Amazon SQS takes at most 10 message attributes." : null;
    }

    private static string FormatAttribute(string name, SqsAttribute attribute) =>
        attribute.DataType == "String" ? $"{name}={attribute.Value}" : $"{name}:{attribute.DataType}={attribute.Value}";

    private List<InfoLine> MessageInfo(OpenQueue queue)
    {
        int held;
        lock (_gate)
        {
            held = _held.Count;
        }

        return
        [
            new("Profile", _session.Profile),
            new("Region", _session.Region),
            new("Queue", queue.Name),
            new("Type", queue.Info.Fifo ? "FIFO" : "Standard"),
            new("Messages received", held.ToString("N0", CultureInfo.CurrentCulture)),
            new("Hidden from consumers", _settings.VisibilitySeconds > 0 ? $"{_settings.VisibilitySeconds} s, released when you leave the queue" : "no (visibility 0)"),
            new("Dead-letter queue", queue.Info.MaxReceiveCount is { } max ? $"{queue.Info.DeadLetterQueue} after {max} receives" : "(none)"),
            new("Credentials", _session.CredentialsDescription),
        ];
    }

    private static PanelEntry MessageEntry(SqsMessage message)
    {
        var receives = message.SystemAttributes.TryGetValue("ApproximateReceiveCount", out var r) && long.TryParse(r, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : (long?)null;
        var sent = Timestamp(message, "SentTimestamp");
        var group = message.SystemAttributes.TryGetValue("MessageGroupId", out var g) ? g : string.Empty;
        return new PanelEntry(message.Id, Size: Encoding.UTF8.GetByteCount(message.Body), Modified: sent)
        {
            Tag = message,
            Cells = new Dictionary<string, CellValue>
            {
                [SentKey] = sent is { } time ? new(time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), time) : new("?", null),
                [ReceivesKey] = receives is { } n ? new(n.ToString("N0", CultureInfo.CurrentCulture), n) : new("?", null),
                [GroupKey] = group,
                [BodyKey] = new(Preview(message.Body), message.Body),
            },
            Tone = receives > 1 ? RowTone.Warning : RowTone.Normal,
        };
    }

    /// <summary>F3: what the console's message details show, then the body (indented when it is JSON).</summary>
    internal string MessageText(SqsMessage message)
    {
        var text = new StringBuilder();
        void Line(string label, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{label + ":",-24}{value}");
            }
        }

        var attributes = message.SystemAttributes;
        Line("Message ID", message.Id);
        Line("Queue", _queue?.Name);
        Line("Sent", Timestamp(message, "SentTimestamp")?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        Line("First received", Timestamp(message, "ApproximateFirstReceiveTimestamp")?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        Line("Receive count", attributes.GetValueOrDefault("ApproximateReceiveCount"));
        Line("Sender", attributes.GetValueOrDefault("SenderId"));
        Line("Message group ID", attributes.GetValueOrDefault("MessageGroupId"));
        Line("Deduplication ID", attributes.GetValueOrDefault("MessageDeduplicationId"));
        Line("Sequence number", attributes.GetValueOrDefault("SequenceNumber"));
        Line("Dead-letter source", attributes.GetValueOrDefault("DeadLetterQueueSourceArn"));
        Line("Body size", $"{Encoding.UTF8.GetByteCount(message.Body):N0} bytes");
        Line("MD5 of body", message.BodyMd5);
        Line("Receipt handle", message.ReceiptHandle);
        foreach (var (name, value) in attributes.Where(a => !KnownSystemAttributes.Contains(a.Key)).OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            Line(name, value);
        }

        if (message.Attributes.Count > 0)
        {
            text.AppendLine().AppendLine("Message attributes:");
            foreach (var (name, value) in message.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {name} ({value.DataType}): {value.Value}");
            }
        }

        text.AppendLine().AppendLine("Body:").Append(PrettyBody(message.Body));
        return text.ToString();
    }

    private static readonly HashSet<string> KnownSystemAttributes = new(StringComparer.Ordinal)
    {
        "SentTimestamp", "ApproximateFirstReceiveTimestamp", "ApproximateReceiveCount", "SenderId", "MessageGroupId",
        "MessageDeduplicationId", "SequenceNumber", "DeadLetterQueueSourceArn",
    };

    private static string QuickBody(SqsMessage message)
    {
        var body = PrettyBody(message.Body);
        return body.Length > QuickViewChars ? body[..QuickViewChars] + Environment.NewLine + "…" : body;
    }

    /// <summary>A JSON body indented; anything else as it is.</summary>
    internal static string PrettyBody(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.Length > 0 && trimmed[0] is '{' or '[')
        {
            try
            {
                return JsonNode.Parse(body)?.ToJsonString(Pretty) ?? body;
            }
            catch (JsonException)
            {
            }
        }

        return body;
    }

    /// <summary>The body on one line for the Body column.</summary>
    internal static string Preview(string body)
    {
        var text = new StringBuilder(Math.Min(body.Length, 200));
        var space = false;
        foreach (var c in body)
        {
            if (char.IsWhiteSpace(c))
            {
                space = text.Length > 0;
                continue;
            }

            if (space)
            {
                text.Append(' ');
                space = false;
            }

            text.Append(c);
            if (text.Length >= 200)
            {
                break;
            }
        }

        return text.ToString();
    }

    /// <summary>"ID.json" when the body is JSON, else "ID.txt"; characters a file name cannot have become "_".</summary>
    internal static string FileNameOf(SqsMessage message)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var id = new string(message.Id.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        if (id.Length == 0)
        {
            id = "message";
        }

        return id + (IsJson(message.Body) ? ".json" : ".txt");
    }

    private static bool IsJson(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not ('{' or '['))
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(body);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DateTime? Timestamp(SqsMessage message, string attribute) =>
        message.SystemAttributes.TryGetValue(attribute, out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)
            ? DateTime.UnixEpoch.AddMilliseconds(milliseconds)
            : null;

    private async Task<IReadOnlyDictionary<string, string>> DeleteMessagesAsync(OpenQueue queue, IReadOnlyList<SqsMessage> messages, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> failed;
        try
        {
            failed = await Api.DeleteAsync(queue.Url, messages, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw AwsErrors.Describe(ex, $"Deleting messages from {queue.Name}", _session);
        }

        lock (_gate)
        {
            foreach (var message in messages.Where(m => !failed.ContainsKey(m.Id)))
            {
                _held.Remove(message.Id);
            }
        }

        return failed;
    }

    private static OperationOutcome Outcome(int count, IReadOnlyDictionary<string, string> failed, string verb) =>
        new(count - failed.Count, 0, failed.Count, failed.Count > 0 ? $"{count - failed.Count} {verb}, {failed.Count} not: {Reasons(failed)}" : null);

    private static string Reasons(IReadOnlyDictionary<string, string> failed) =>
        string.Join("; ", failed.Take(5).Select(f => $"{f.Key}: {f.Value}")) + (failed.Count > 5 ? "; …" : string.Empty);

    /// <summary>Makes the messages still held visible to consumers again. Best effort: a handle may have expired.</summary>
    private async Task ReleaseAsync()
    {
        if (_queue is not { } queue || _api is null)
        {
            return;
        }

        List<SqsMessage> held;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            held = _held.Values.Where(h => h.HiddenUntil > now).Select(h => h.Message).ToList();
            _held.Clear();
        }

        if (held.Count == 0)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _api.ChangeVisibilityAsync(queue.Url, held, 0, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Amazon.Runtime.AmazonServiceException or Amazon.Runtime.AmazonClientException or OperationCanceledException or PluginException or HttpRequestException or IOException)
        {
            // They become visible by themselves when the visibility timeout ends.
        }
    }

    private bool IsWarned(string url)
    {
        lock (_warned)
        {
            return _warned.Contains(url);
        }
    }

    private sealed record OpenQueue(string Url, string Name, QueueInfo Info);

    private sealed record Held(SqsMessage Message, DateTime HiddenUntil);
}
