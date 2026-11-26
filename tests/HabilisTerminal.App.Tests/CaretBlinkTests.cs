using HabilisTerminal.App.Hosting;
using Xunit;

namespace HabilisTerminal.App.Tests;

/// <summary>The console cursor follows the Windows caret settings, and stops blinking when idle (idle CPU).</summary>
public sealed class CaretBlinkTests
{
    [Fact]
    public void Blink_rate_comes_from_the_system_and_infinite_means_no_blinking()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(400), CaretBlink.IntervalOf(400));
        Assert.Null(CaretBlink.IntervalOf(0xFFFFFFFF));
        Assert.Equal(TimeSpan.FromMilliseconds(530), CaretBlink.IntervalOf(0));
    }

    [Fact]
    public void Blinking_stops_after_the_system_caret_timeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), CaretBlink.TimeoutOf(5000));
        Assert.Equal(Timeout.InfiniteTimeSpan, CaretBlink.TimeoutOf(0xFFFFFFFF));
        Assert.Equal(TimeSpan.FromSeconds(5), CaretBlink.TimeoutOf(0));
    }

    [Fact]
    public void This_machine_gives_sensible_values()
    {
        Assert.True(CaretBlink.Timeout() > TimeSpan.Zero || CaretBlink.Timeout() == Timeout.InfiniteTimeSpan);
        Assert.True(CaretBlink.Interval() is null or { TotalMilliseconds: >= 100 });
    }
}
