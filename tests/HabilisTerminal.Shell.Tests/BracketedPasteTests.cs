using HabilisTerminal.Shell.Integration;

namespace HabilisTerminal.Shell.Tests;

public class BracketedPasteTests
{
    [Fact]
    public void Follows_set_and_reset_even_when_split_between_chunks()
    {
        var tracker = new BracketedPasteTracker();
        Assert.False(tracker.Enabled);

        tracker.Feed("prompt \u001b[?20");
        tracker.Feed("04h> ");
        Assert.True(tracker.Enabled);

        tracker.Feed("output \u001b[?2004l and \u001b[?2004h again \u001b[?2004l");
        Assert.False(tracker.Enabled);

        tracker.Feed("plain text without modes");
        Assert.False(tracker.Enabled);
    }

    [Fact]
    public void Wraps_only_when_enabled_and_sends_line_breaks_as_cr()
    {
        var tracker = new BracketedPasteTracker();
        Assert.Equal("a\rb\rc", tracker.Wrap("a\r\nb\nc"));

        tracker.Feed("\u001b[?2004h");
        Assert.Equal("\u001b[200~a\rb\u001b[201~", tracker.Wrap("a\nb"));
        Assert.Equal("\u001b[200~xy\u001b[201~", tracker.Wrap("x\u001b[201~y"));
    }
}
