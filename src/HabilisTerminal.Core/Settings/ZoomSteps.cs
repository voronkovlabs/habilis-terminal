using System.Globalization;

namespace HabilisTerminal.Core.Settings;

/// <summary>
/// The interface zoom's steps (issue #237), as a browser has them: Ctrl+wheel and Ctrl+Alt+= / − move one step, Ctrl+Alt+0
/// goes back to 100%.
/// </summary>
public static class ZoomSteps
{
    public static IReadOnlyList<double> All { get; } = [0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

    public static double Minimum => All[0];

    public static double Maximum => All[^1];

    /// <summary>The next step up (<paramref name="direction"/> &gt; 0) or down from any factor, which need not be a step.</summary>
    public static double Next(double factor, int direction)
    {
        const double Tolerance = 0.001;
        if (direction > 0)
        {
            return All.FirstOrDefault(s => s > factor + Tolerance, Maximum);
        }

        return direction < 0 ? All.LastOrDefault(s => s < factor - Tolerance, Minimum) : Clamp(factor);
    }

    /// <summary>A stored or outside factor kept within the steps' range; NaN or nothing is 100%.</summary>
    public static double Clamp(double factor) => double.IsFinite(factor) && factor > 0 ? Math.Clamp(factor, Minimum, Maximum) : 1.0;

    public static string Percent(double factor) => ((int)Math.Round(factor * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>A font size in the zoom, rounded to whole pixels, as the console takes it.</summary>
    public static int FontSize(int size, double factor) => Math.Max(1, (int)Math.Round(size * factor));
}
