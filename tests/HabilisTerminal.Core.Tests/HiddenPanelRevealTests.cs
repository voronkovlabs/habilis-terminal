using HabilisTerminal.Core.Panels;

namespace HabilisTerminal.Core.Tests;

public class HiddenPanelRevealTests
{
    private readonly object _left = new();
    private readonly object _right = new();

    [Theory]
    // A hidden panel always gets the content, even if it kept it while hidden.
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    // A shown panel toggles.
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void A_toggle_on_a_hidden_panel_always_turns_the_content_on(bool shown, bool hasContent, bool expected)
    {
        Assert.Equal(expected, HiddenPanelReveal.TurnsOn(shown, hasContent));
    }

    [Fact]
    public void Content_in_a_hidden_panel_shows_it_and_turning_it_off_hides_it_again()
    {
        var reveal = new HiddenPanelReveal();

        Assert.True(reveal.ContentOn(_right, targetShown: false));
        Assert.Same(_right, reveal.Revealed);
        Assert.True(reveal.ContentOff(_right));
        Assert.Null(reveal.Revealed);
    }

    [Fact]
    public void Switching_between_info_and_quick_view_keeps_the_panel_shown_and_remembered()
    {
        var reveal = new HiddenPanelReveal();
        reveal.ContentOn(_right, targetShown: false);

        // Ctrl+L after Ctrl+Q: the panel is shown already.
        Assert.False(reveal.ContentOn(_right, targetShown: true));
        Assert.Same(_right, reveal.Revealed);
        Assert.True(reveal.ContentOff(_right));
    }

    [Fact]
    public void A_panel_that_was_shown_already_stays_shown_when_the_content_goes()
    {
        var reveal = new HiddenPanelReveal();

        Assert.False(reveal.ContentOn(_right, targetShown: true));
        Assert.False(reveal.ContentOff(_right));
    }

    [Fact]
    public void After_another_layout_change_the_panel_belongs_to_the_layout()
    {
        var reveal = new HiddenPanelReveal();
        reveal.ContentOn(_right, targetShown: false);

        reveal.Forget();

        Assert.False(reveal.ContentOff(_right));
    }

    [Fact]
    public void Content_turned_off_in_the_other_panel_does_not_hide_the_revealed_one()
    {
        var reveal = new HiddenPanelReveal();
        reveal.ContentOn(_right, targetShown: false);

        Assert.False(reveal.ContentOff(_left));
        Assert.Same(_right, reveal.Revealed);
    }
}
