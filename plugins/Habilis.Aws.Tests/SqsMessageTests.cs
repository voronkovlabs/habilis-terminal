using Habilis.Aws.Sqs;
using HabilisTerminal.Plugins;

namespace Habilis.Aws.Tests;

/// <summary>Issue #397: browsing, viewing, deleting, saving and sending the messages of a queue.</summary>
public sealed class SqsMessageTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "habi-tests", "sqs-" + Guid.NewGuid().ToString("N"));

    public SqsMessageTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Enter_receives_the_messages_in_batches_and_leaving_releases_them()
    {
        var api = new FakeSqs(["orders", "payments"]);
        AddMessages(api, "orders", 25);
        var panel = await SqsTests.OpenAsync(new FakeHost(), api, "sqs:prod/eu-west-1");

        await EnterAsync(panel, "orders");
        var batches = new List<int>();
        await foreach (var batch in panel.ListAsync(CancellationToken.None))
        {
            batches.Add(batch.Count);
        }

        Assert.Equal([10, 10, 5], batches);
        Assert.Equal("sqs:prod/eu-west-1/orders", panel.Location);
        Assert.True(panel.ShowsParent);
        Assert.Equal(2, api.Receives[0].Wait);
        Assert.All(api.Receives.Skip(1), r => Assert.Equal(1, r.Wait));
        Assert.All(api.Receives, r => Assert.Equal(30, r.Visibility));
        Assert.All(api.Queues["orders"], m => Assert.False(m.Visible));

        Assert.True(await panel.GoUpAsync(CancellationToken.None));

        Assert.Equal("sqs:prod/eu-west-1", panel.Location);
        Assert.Equal(25, api.VisibilityChanges.Count(c => c.Seconds == 0));
        Assert.All(api.Queues["orders"], m => Assert.True(m.Visible));
        Assert.Equal(["orders", "payments"], (await Listing.AllAsync(panel)).Select(e => e.Name));
    }

    [Fact]
    public async Task A_refresh_keeps_the_messages_held_and_receives_only_new_ones()
    {
        var api = new FakeSqs(["orders"]);
        AddMessages(api, "orders", 3);
        var panel = await SqsTests.OpenAsync(new FakeHost(), api, "sqs:prod/eu-west-1");
        await EnterAsync(panel, "orders");
        Assert.Equal(3, (await Listing.AllAsync(panel)).Count);

        api.Add("orders", "new");
        var entries = await Listing.AllAsync(panel);

        Assert.Equal(4, entries.Count);
        Assert.All(api.Queues["orders"], m => Assert.Equal(1, m.ReceiveCount));
    }

    [Fact]
    public async Task Receiving_stops_at_the_limit_from_the_settings()
    {
        var api = new FakeSqs(["orders"]);
        AddMessages(api, "orders", 25);
        var host = new FakeHost();
        new SqsSettings { ReceiveLimit = 12, WaitSeconds = 0, VisibilitySeconds = 60 }.Save(host.Settings);
        var panel = await SqsTests.OpenAsync(host, api, "sqs:prod/eu-west-1");
        await EnterAsync(panel, "orders");

        var entries = await Listing.AllAsync(panel);

        Assert.Equal(12, entries.Count);
        Assert.Equal([10, 2], api.Receives.Select(r => r.Max));
        Assert.Equal(0, api.Receives[0].Wait);
        Assert.All(api.Receives, r => Assert.Equal(60, r.Visibility));
    }

    [Fact]
    public async Task With_visibility_zero_messages_received_again_are_listed_once()
    {
        var api = new FakeSqs(["orders"]);
        AddMessages(api, "orders", 4);
        var host = new FakeHost();
        new SqsSettings { VisibilitySeconds = 0 }.Save(host.Settings);
        var panel = await SqsTests.OpenAsync(host, api, "sqs:prod/eu-west-1");
        await EnterAsync(panel, "orders");

        var entries = await Listing.AllAsync(panel);

        Assert.Equal(4, entries.Count);
        Assert.Equal(2, api.Receives.Count);
        Assert.All(api.Queues["orders"], m => Assert.True(m.Visible));
    }

    [Fact]
    public async Task A_queue_with_a_dead_letter_queue_asks_once_before_receiving()
    {
        var api = new FakeSqs(["orders"]);
        api.Infos["orders"] = new QueueInfo(false, 30, "orders-dlq", 3);
        AddMessages(api, "orders", 2);
        var host = new FakeHost();
        var panel = await SqsTests.OpenAsync(host, api, "sqs:prod/eu-west-1");
        var queue = (await Listing.AllAsync(panel)).Single();

        host.ConfirmAnswers.Enqueue(false);
        Assert.False(await panel.EnterAsync(queue, CancellationToken.None));
        Assert.Empty(api.Receives);
        Assert.Contains("after 3 receives Amazon SQS moves a message to orders-dlq", host.Confirmations[0].Message, StringComparison.Ordinal);

        host.ConfirmAnswers.Enqueue(true);
        Assert.True(await panel.EnterAsync(queue, CancellationToken.None));
        Assert.True(await panel.GoUpAsync(CancellationToken.None));
        Assert.True(await panel.EnterAsync(queue, CancellationToken.None));
        Assert.Equal(2, host.Confirmations.Count);
        Assert.Contains(panel.Info, l => l is { Label: "Dead-letter queue", Value: "orders-dlq after 3 receives" });
    }

    [Fact]
    public async Task F3_shows_the_details_and_the_body_indented_and_Enter_on_a_message_does_not_go_in()
    {
        var api = new FakeSqs(["orders"]);
        api.Add("orders", """{"order":42,"items":["a"]}""");
        var panel = await SqsTests.OpenAsync(new FakeHost(), api, "sqs:prod/eu-west-1");
        await EnterAsync(panel, "orders");
        var message = (await Listing.AllAsync(panel)).Single();

        var view = await panel.ViewAsync(message, CancellationToken.None);
        var quick = await panel.QuickViewAsync(message, CancellationToken.None);

        Assert.Equal("m001", view!.Value.Title);
        Assert.Contains("Receive count:          1", view.Value.Text, StringComparison.Ordinal);
        Assert.Contains("source (String): test", view.Value.Text, StringComparison.Ordinal);
        Assert.Contains("\"order\": 42", view.Value.Text, StringComparison.Ordinal);
        Assert.StartsWith("{", quick, StringComparison.Ordinal);
        Assert.Contains(Environment.NewLine, quick, StringComparison.Ordinal);
        Assert.Equal("""{"order":42,"items":["a"]}""", message.Cells![SqsPanel.BodyKey].Text);
        Assert.Equal("m001", ((IPanelProvider)panel).EntryAddress(message));
        Assert.False(await panel.EnterAsync(message, CancellationToken.None));
    }

    [Fact]
    public async Task F8_deletes_the_messages_and_reports_one_whose_handle_expired()
    {
        var api = new FakeSqs(["orders"]);
        AddMessages(api, "orders", 3);
        var panel = await SqsTests.OpenAsync(new FakeHost(), api, "sqs:prod/eu-west-1");
        await EnterAsync(panel, "orders");
        var entries = await Listing.AllAsync(panel);
        api.Queues["orders"].Single(m => m.Id == "m002").Handle = "changed";

        var outcome = await panel.DeleteAsync(entries, new FakeProgress(), CancellationToken.None);

        Assert.Equal(2, outcome.Succeeded);
        Assert.Equal(1, outcome.Failed);
        Assert.Contains("m002: The receipt handle has expired.", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(["m002"], api.Queues["orders"].Select(m => m.Id));
        Assert.Equal(1, panel.Commands.Count(c => c.Key is not null));
    }

    [Fact]
    public async Task F5_saves_bodies_as_files_and_F6_also_deletes_the_messages()
    {
        var api = new FakeSqs(["orders"]);
        api.Add("orders", """{"a":1}""");
        api.Add("orders", "plain text");
        var panel = await SqsTests.OpenAsync(new FakeHost(), api, "sqs:prod/eu-west-1");
        await EnterAsync(panel, "orders");
        var entries = await Listing.AllAsync(panel);

        var copy = await panel.GetFilesAsync(new TransferRequest(entries, _folder, move: false, _ => Task.FromResult(OverwriteChoice.Skip)), new FakeProgress(), CancellationToken.None);

        Assert.Equal(2, copy.Succeeded);
        Assert.Equal("""{"a":1}""", await File.ReadAllTextAsync(Path.Combine(_folder, "m001.json")));
        Assert.Equal("plain text", await File.ReadAllTextAsync(Path.Combine(_folder, "m002.txt")));
        Assert.Equal(2, api.Queues["orders"].Count);

        var again = await panel.GetFilesAsync(new TransferRequest(entries, _folder, move: true, _ => Task.FromResult(OverwriteChoice.SkipAll)), new FakeProgress(), CancellationToken.None);
        Assert.Equal(2, again.Skipped);
        Assert.Equal(2, api.Queues["orders"].Count);

        var move = await panel.GetFilesAsync(new TransferRequest(entries, _folder, move: true, _ => Task.FromResult(OverwriteChoice.OverwriteAll)), new FakeProgress(), CancellationToken.None);
        Assert.Equal(2, move.Succeeded);
        Assert.Empty(api.Queues["orders"]);
        Assert.Empty(Directory.EnumerateFiles(_folder, "*.habi-part"));
    }

    [Fact]
    public async Task F5_of_files_into_a_queue_sends_each_file_as_a_message()
    {
        var api = new FakeSqs(["orders"]);
        var panel = await SqsTests.OpenAsync(new FakeHost(), api, "sqs:prod/eu-west-1");
        await Assert.ThrowsAsync<PluginException>(() => panel.PutFilesAsync([_folder], _ => Task.FromResult(OverwriteChoice.Skip), new FakeProgress(), CancellationToken.None));

        await File.WriteAllTextAsync(Path.Combine(_folder, "a.json"), """{"n":1}""");
        Directory.CreateDirectory(Path.Combine(_folder, "more"));
        await File.WriteAllTextAsync(Path.Combine(_folder, "more", "b.txt"), "two\nlines");
        await File.WriteAllBytesAsync(Path.Combine(_folder, "big.bin"), new byte[SqsPanel.MaxFileBytes + 1]);
        await EnterAsync(panel, "orders");

        var outcome = await panel.PutFilesAsync([Path.Combine(_folder, "a.json"), Path.Combine(_folder, "more"), Path.Combine(_folder, "big.bin")], _ => Task.FromResult(OverwriteChoice.Skip), new FakeProgress(), CancellationToken.None);

        Assert.Equal(2, outcome.Succeeded);
        Assert.Equal(1, outcome.Failed);
        Assert.Contains("big.bin: larger than 1 MiB", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(["""{"n":1}""", "two\nlines"], api.Sent.Select(s => s.Message.Body));
        Assert.All(api.Sent, s => Assert.Null(s.Message.GroupId));
    }

    [Fact]
    public async Task Files_into_a_FIFO_queue_ask_for_the_group_and_get_their_own_deduplication_IDs()
    {
        var api = new FakeSqs(["jobs.fifo"]);
        var host = new FakeHost();
        host.InputAnswers.Enqueue("batch-7");
        var panel = await SqsTests.OpenAsync(host, api, "sqs:prod/eu-west-1");
        await EnterAsync(panel, "jobs.fifo");
        await File.WriteAllTextAsync(Path.Combine(_folder, "a.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(_folder, "b.txt"), "same");

        await panel.PutFilesAsync([Path.Combine(_folder, "a.txt"), Path.Combine(_folder, "b.txt")], _ => Task.FromResult(OverwriteChoice.Skip), new FakeProgress(), CancellationToken.None);

        Assert.All(api.Sent, s => Assert.Equal("batch-7", s.Message.GroupId));
        Assert.Equal(2, api.Sent.Select(s => s.Message.DeduplicationId).Distinct().Count());
    }

    [Fact]
    public async Task Shift_F4_sends_the_form_to_the_queue_under_the_cursor()
    {
        var api = new FakeSqs(["orders"]);
        var host = new FakeHost
        {
            EditForm = form =>
            {
                form.Field<TextField>("body").Value = "line 1\nline 2\n";
                form.Field<NumberField>("delay").Value = 5;
                form.Field<ListField>("attributes").Values = ["kind=test", "count:Number=3"];
                return form.Validate!(form) is null;
            },
        };
        var panel = await SqsTests.OpenAsync(host, api, "sqs:prod/eu-west-1");
        var queue = (await Listing.AllAsync(panel)).Single();

        await panel.Commands.Single(c => c.Key == "Shift+F4").Run(new PanelCommandContext(queue, [queue], new FakeProgress(), CancellationToken.None));

        var (name, message) = Assert.Single(api.Sent);
        Assert.Equal("orders", name);
        Assert.Equal("line 1\nline 2\n", message.Body);
        Assert.Equal(5, message.DelaySeconds);
        Assert.Equal(new SqsAttribute("String", "test"), message.Attributes!["kind"]);
        Assert.Equal(new SqsAttribute("Number", "3"), message.Attributes!["count"]);
        Assert.Contains(host.Messages, m => m.Message.StartsWith("Sent to orders: message", StringComparison.Ordinal));
    }

    [Fact]
    public void The_send_form_checks_FIFO_groups_and_attributes()
    {
        var fifo = new QueueInfo(true, 30);
        var form = SqsPanel.SendForm("jobs.fifo", fifo, null);
        form.Field<TextField>("body").Value = "x";
        Assert.Equal("A FIFO queue needs a message group ID.", form.Validate!(form));

        form.Field<TextField>("group").Value = "g";
        Assert.Null(form.Validate(form));
        var outgoing = SqsPanel.FromSendForm(form, fifo);
        Assert.Equal("g", outgoing.GroupId);
        Assert.NotNull(outgoing.DeduplicationId);
        Assert.Equal(0, outgoing.DelaySeconds);
        Assert.Null(SqsPanel.FromSendForm(form, fifo with { ContentDeduplication = true }).DeduplicationId);
        Assert.DoesNotContain(form.Fields, f => f.Key == "delay");
        Assert.Equal(10, form.Field<TextField>("body").Lines);

        Assert.Contains("not Name=value", SqsPanel.ParseAttributes(["oops"], out _), StringComparison.Ordinal);
        Assert.Contains("not a number", SqsPanel.ParseAttributes(["n:Number=abc"], out _), StringComparison.Ordinal);
        Assert.Contains("String, Number or Binary", SqsPanel.ParseAttributes(["n:Date=1"], out _), StringComparison.Ordinal);
        Assert.Null(SqsPanel.ParseAttributes(["id:String.uuid=a=b", "raw:Binary=AQID"], out var parsed));
        Assert.Equal(new SqsAttribute("String.uuid", "a=b"), parsed["id"]);
    }

    [Fact]
    public async Task A_queue_location_opens_the_queue_list_with_the_cursor_on_it()
    {
        var panel = await SqsTests.OpenAsync(new FakeHost(), new FakeSqs(["orders"]), "sqs:prod/eu-west-1/orders");

        Assert.Equal("sqs:prod/eu-west-1", panel.Location);
        Assert.Equal("orders", panel.InitialCursor);
    }

    [Theory]
    [InlineData("""{"deadLetterTargetArn":"arn:aws:sqs:eu-west-1:1:orders-dlq","maxReceiveCount":"5"}""", "orders-dlq", 5)]
    [InlineData("""{"deadLetterTargetArn":"arn:aws:sqs:eu-west-1:1:x","maxReceiveCount":3}""", "x", 3)]
    [InlineData("not json", null, null)]
    public void Redrive_policies_give_the_dead_letter_queue_and_the_limit(string policy, string? queue, int? max) =>
        Assert.Equal((queue, max), SqsApi.ParseRedrive(policy));

    [Theory]
    [InlineData("{\n  \"a\" :  1 }\n", "{ \"a\" : 1 }")]
    [InlineData("  plain\r\n text ", "plain text")]
    public void The_body_column_shows_the_body_on_one_line(string body, string expected) =>
        Assert.Equal(expected, SqsPanel.Preview(body));

    private static void AddMessages(FakeSqs api, string queue, int count)
    {
        for (var i = 0; i < count; i++)
        {
            api.Add(queue, $"body {i}");
        }
    }

    private static async Task EnterAsync(SqsPanel panel, string queue)
    {
        var entry = (await Listing.AllAsync(panel)).Single(e => e.Name == queue);
        Assert.True(await panel.EnterAsync(entry, CancellationToken.None));
    }
}
