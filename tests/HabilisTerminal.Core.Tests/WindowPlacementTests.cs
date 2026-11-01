using HabilisTerminal.Core.Settings;

namespace HabilisTerminal.Core.Tests;

public class WindowPlacementTests
{
    [Theory]
    [InlineData(1, 0, false)] // normal
    [InlineData(3, 0, true)] // maximized
    [InlineData(2, 0, false)] // minimized from normal
    [InlineData(2, 2, true)] // minimized from maximized (WPF_RESTORETOMAXIMIZED)
    [InlineData(1, 2, false)]
    public void IsMaximized_maps_showCmd_and_flags(int showCmd, int flags, bool expected) =>
        Assert.Equal(expected, WindowPlacement.IsMaximized(showCmd, flags));
}
