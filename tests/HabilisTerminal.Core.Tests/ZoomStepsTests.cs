using HabilisTerminal.Core.Settings;

namespace HabilisTerminal.Core.Tests;

/// <summary>The interface zoom's steps (issue #237).</summary>
public class ZoomStepsTests
{
    [Theory]
    [InlineData(1.0, 1, 1.1)]
    [InlineData(1.0, -1, 0.9)]
    [InlineData(1.1, -1, 1.0)]
    [InlineData(3.0, 1, 3.0)]
    [InlineData(0.5, -1, 0.5)]
    [InlineData(1.2, 1, 1.25)] // a factor between steps (WebView2's own zoom) goes to the next step
    [InlineData(1.2, -1, 1.1)]
    public void A_step_goes_to_the_next_zoom(double factor, int direction, double expected) =>
        Assert.Equal(expected, ZoomSteps.Next(factor, direction), 3);

    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(0, 1.0)]
    [InlineData(-2, 1.0)]
    [InlineData(0.1, 0.5)]
    [InlineData(9, 3.0)]
    [InlineData(1.25, 1.25)]
    public void A_stored_zoom_is_kept_in_range(double factor, double expected) =>
        Assert.Equal(expected, ZoomSteps.Clamp(factor), 3);

    [Fact]
    public void The_badge_and_the_console_font_round()
    {
        Assert.Equal("125%", ZoomSteps.Percent(1.25));
        Assert.Equal("67%", ZoomSteps.Percent(0.67));
        Assert.Equal(18, ZoomSteps.FontSize(12, 1.5));
        Assert.Equal(8, ZoomSteps.FontSize(12, 0.67));
    }

    [Fact]
    public void The_zoom_is_remembered_in_the_state()
    {
        var folder = Path.Combine(Path.GetTempPath(), "habi-tests", "zoom-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var store = new StateStore(Path.Combine(folder, "state.json"));
            Assert.Null(store.Load().Zoom);
            store.Save(new AppState { Zoom = 1.25 });
            Assert.Equal(1.25, store.Load().Zoom);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
