namespace HabilisTerminal.App.Input;

/// <summary>
/// Whole steps out of wheel deltas (issue #275). A mouse wheel's notch is 1; a Mac's trackpad sends many fractions in
/// a row, which counted as a step each scrolled the panels and flipped pictures far too fast. The fractions add up, and a
/// turn of direction starts over.
/// </summary>
public sealed class WheelSteps
{
    private double _rest;

    /// <summary>The whole steps <paramref name="delta"/> brings, times <paramref name="perNotch"/>; the rest is kept.</summary>
    public int Take(double delta, double perNotch = 1)
    {
        if (delta == 0)
        {
            return 0;
        }

        if (Math.Sign(delta) != Math.Sign(_rest))
        {
            _rest = 0;
        }

        _rest += delta * perNotch;
        var whole = (int)Math.Truncate(_rest);
        _rest -= whole;
        return whole;
    }
}
