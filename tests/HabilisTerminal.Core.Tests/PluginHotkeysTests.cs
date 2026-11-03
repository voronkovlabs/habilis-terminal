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
}
