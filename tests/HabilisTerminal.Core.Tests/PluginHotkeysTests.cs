using HabilisTerminal.Core.Plugins;

namespace HabilisTerminal.Core.Tests;

public class PluginHotkeysTests
{
    [Fact]
    public void A_key_moves_from_one_item_to_another_and_survives_a_round_trip()
    {
        var hotkeys = new PluginHotkeys();
        Assert.Null(hotkeys.Assign("aws.s3", "S3 buckets", 's', ['C', 'D']));
        Assert.Null(hotkeys.Assign("aws.sqs", "SQS queues", '1', ['C', 'D']));
        Assert.Null(hotkeys.Assign("aws.sqs", "SQS queues", 'S', ['C', 'D'])); // takes S from S3

        var restored = new PluginHotkeys(hotkeys.Snapshot());
        Assert.Equal('S', restored.Get("aws.sqs", "SQS queues"));
        Assert.Null(restored.Get("aws.s3", "S3 buckets"));

        Assert.Null(restored.Assign("aws.sqs", "SQS queues", null, []));
        Assert.Empty(restored.Snapshot());
    }

    [Theory]
    [InlineData('c', "already picks drive C:")]
    [InlineData('-', "letter or a digit")]
    [InlineData('é', "letter or a digit")]
    public void Drive_letters_and_other_characters_are_refused(char key, string reason)
    {
        var hotkeys = new PluginHotkeys(new Dictionary<string, string> { ["x/y"] = "7", ["bad/entry"] = "??" });
        Assert.Contains(reason, hotkeys.Assign("aws.s3", "S3 buckets", key, ['C']), StringComparison.Ordinal);
        Assert.Null(hotkeys.Get("aws.s3", "S3 buckets"));
        Assert.Equal('7', hotkeys.Get("x", "y"));
        Assert.Single(hotkeys.Snapshot());
    }

    [Fact]
    public void Items_without_an_assigned_key_get_digits_in_order_skipping_taken_keys()
    {
        var hotkeys = new PluginHotkeys();
        hotkeys.Assign("places", "Downloads", '2', ['C']);
        IReadOnlyList<(string, string)> items = [("aws.s3", "S3 buckets"), ("aws.sqs", "SQS queues"), ("places", "Home"), ("places", "Downloads")];

        Assert.Equal(['1', '3', '4', '2'], hotkeys.Resolve(items, ['C']).Select(k => k!.Value));

        // Past the digits come the letters no drive uses.
        var many = Enumerable.Range(0, 12).Select(i => ("p", $"item {i}")).ToList();
        Assert.Equal("1234567890BD", string.Concat(new PluginHotkeys().Resolve(many, ['A', 'C']).Select(k => k!.Value)));
    }
}
