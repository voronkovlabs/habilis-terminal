using HabilisTerminal.Core.Panels;

namespace HabilisTerminal.Core.Tests;

public class ConsoleTabRulesTests
{
    [Theory]
    [InlineData(0, 3, 1, 1)]
    [InlineData(2, 3, 1, 0)]
    [InlineData(0, 3, -1, 2)]
    [InlineData(1, 2, -1, 0)]
    public void Next_wraps_around(int current, int count, int direction, int expected) =>
        Assert.Equal(expected, ConsoleTabRules.Next(current, count, direction));

    [Theory]
    [InlineData(0, 2, 0)]
    [InlineData(1, 2, 1)]
    [InlineData(2, 2, 1)]
    public void After_closing_the_neighbour_takes_over(int closed, int remaining, int expected) =>
        Assert.Equal(expected, ConsoleTabRules.AfterClosing(closed, remaining));

    [Fact]
    public void A_new_tab_takes_the_first_free_colour()
    {
        Assert.Equal(0, ConsoleTabRules.FreeColor([], 3));
        Assert.Equal(1, ConsoleTabRules.FreeColor([0, 2], 3));
        Assert.Equal(1, ConsoleTabRules.FreeColor([0, 1, 2, 0], 3));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void Closing_asks_only_with_running_commands(int running, bool expected) =>
        Assert.Equal(expected, ConsoleTabRules.AskBeforeClosing(running));
}
