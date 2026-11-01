using HabilisTerminal.Core.Panels;

namespace HabilisTerminal.Core.Tests;

public class PanelVisibilityRuleTests
{
    private static readonly (bool, bool) None = (false, false);
    private static readonly (bool, bool) Left = (true, false);
    private static readonly (bool, bool) Right = (false, true);
    private static readonly (bool, bool) Both = (true, true);

    [Theory]
    // Ctrl+P / Ctrl+F1 / Ctrl+F2: the panel that was visible keeps priority over the one that appears.
    [InlineData(true, false, true, true, true, false)]
    [InlineData(false, false, true, true, true, false)]
    [InlineData(true, true, false, true, true, true)]
    // Ctrl+O from no panels: the active panel stays active.
    [InlineData(true, false, false, true, true, true)]
    [InlineData(false, false, false, true, true, false)]
    // The active panel is hidden: the one still shown takes over.
    [InlineData(true, true, true, false, true, false)]
    [InlineData(false, true, true, true, false, true)]
    // Both hidden: nothing changes.
    [InlineData(false, true, true, false, false, false)]
    public void A_shown_panel_is_active_and_a_visible_one_keeps_priority(
        bool activeIsLeft, bool leftBefore, bool rightBefore, bool leftAfter, bool rightAfter, bool expectedLeft)
    {
        Assert.Equal(expectedLeft, PanelVisibilityRule.ActiveIsLeft(activeIsLeft, (leftBefore, rightBefore), (leftAfter, rightAfter)));
    }

    [Fact]
    public void A_hidden_active_panel_restored_at_start_gives_way_to_the_visible_one()
    {
        Assert.False(PanelVisibilityRule.ActiveIsLeft(true, Right, Right));
        Assert.True(PanelVisibilityRule.ActiveIsLeft(false, Left, Left));
        Assert.False(PanelVisibilityRule.ActiveIsLeft(false, Both, Both));
        Assert.True(PanelVisibilityRule.ActiveIsLeft(true, None, None));
    }

    [Fact]
    public void A_second_panel_joining_takes_focus_from_the_terminal()
    {
        Assert.True(PanelVisibilityRule.FocusesPanel(Left, Both, terminalHasFocus: true));
        Assert.True(PanelVisibilityRule.FocusesPanel(Right, Both, terminalHasFocus: true));
        Assert.True(PanelVisibilityRule.FocusesPanel(None, Both, terminalHasFocus: true));
    }

    [Fact]
    public void Panels_reappearing_take_focus_and_other_changes_from_the_prompt_keep_it_in_the_terminal()
    {
        Assert.True(PanelVisibilityRule.FocusesPanel(None, Left, terminalHasFocus: true));
        Assert.False(PanelVisibilityRule.FocusesPanel(Both, Left, terminalHasFocus: true));
        Assert.False(PanelVisibilityRule.FocusesPanel(Left, Right, terminalHasFocus: true));
        Assert.False(PanelVisibilityRule.FocusesPanel(Both, None, terminalHasFocus: false));
        Assert.True(PanelVisibilityRule.FocusesPanel(Both, Left, terminalHasFocus: false));
    }
}
