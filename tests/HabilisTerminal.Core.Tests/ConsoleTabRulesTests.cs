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

    [Theory]
    [InlineData(1160, 2, 135, 320)] // plenty of room
    [InlineData(1160, 8, 135, 145)] // the wide minimum still fits
    [InlineData(1160, 16, 72.5, 72.5)] // narrowing, all equal
    [InlineData(1160, 25, 46.4, 46.4)]
    [InlineData(1160, 40, 45, 45)] // the narrow minimum, and the strip scrolls
    public void Tabs_narrow_as_they_are_added(double available, int count, double minimum, double maximum)
    {
        var widths = ConsoleTabRules.TabWidths(available, count);
        Assert.Equal(minimum, widths.Minimum, 3);
        Assert.Equal(maximum, widths.Maximum, 3);
    }

    [Theory]
    [InlineData(1160, 25, false)]
    [InlineData(1160, 26, true)]
    public void The_strip_scrolls_only_when_narrow_tabs_do_not_fit(double available, int count, bool expected) =>
        Assert.Equal(expected, ConsoleTabRules.Scrolls(available, count));

    [Theory]
    [InlineData(7, "0:07")]
    [InlineData(750, "12:30")]
    [InlineData(3723, "1:02:03")]
    public void Elapsed_time_reads_like_a_clock(int seconds, string expected) =>
        Assert.Equal(expected, ConsoleTabRules.FormatElapsed(TimeSpan.FromSeconds(seconds)));

    [WindowsTheory]
    [InlineData(@"C:\Users\me\work\app", @"C:\Users\me", @"~\work")]
    [InlineData(@"C:\Users\me\app", @"C:\Users\me", "~")]
    [InlineData(@"C:\Users\meta\app", @"C:\Users\me", @"C:\Users\meta")]
    [InlineData(@"D:\data\app", @"C:\Users\me", @"D:\data")]
    [InlineData(@"D:\", @"C:\Users\me", "")]
    public void The_parent_folder_is_shortened_at_home(string folder, string home, string expected) =>
        Assert.Equal(expected, ConsoleTabRules.ParentForDisplay(folder, home));

    [UnixTheory]
    [InlineData("/Users/me/work/app", "/Users/me", "~/work")]
    [InlineData("/Users/me/app", "/Users/me", "~")]
    [InlineData("/Users/meta/app", "/Users/me", "/Users/meta")]
    [InlineData("/Volumes/data/app", "/Users/me", "/Volumes/data")]
    [InlineData("/Users", "/Users/me", "/")]
    [InlineData("/", "/Users/me", "")]
    public void The_parent_folder_is_shortened_at_home_on_a_mac(string folder, string home, string expected) =>
        Assert.Equal(expected, ConsoleTabRules.ParentForDisplay(folder, home));

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
