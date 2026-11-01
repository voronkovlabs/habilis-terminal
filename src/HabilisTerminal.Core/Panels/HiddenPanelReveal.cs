namespace HabilisTerminal.Core.Panels;

/// <summary>
/// Ctrl+L / Ctrl+Q with the other panel hidden (issue #38): the content shows that panel, and turning the content off
/// hides it again, so the layout comes back. Any other layout change (Ctrl+P, Ctrl+F1 / Ctrl+F2) makes the shown panel
/// part of the layout, and turning the content off then leaves it shown.
/// </summary>
public sealed class HiddenPanelReveal
{
    /// <summary>The panel shown only for its info or quick view, or null.</summary>
    public object? Revealed { get; private set; }

    /// <summary>
    /// Whether a toggle key turns the content on. On a hidden panel it always does, even if the panel kept that
    /// content while hidden, so the key is never invisible.
    /// </summary>
    public static bool TurnsOn(bool targetShown, bool targetHasContent) => !targetShown || !targetHasContent;

    /// <summary>Content was turned on in <paramref name="target"/>. Returns whether the panel must be shown.</summary>
    public bool ContentOn(object target, bool targetShown)
    {
        if (targetShown)
        {
            return false;
        }

        Revealed = target;
        return true;
    }

    /// <summary>Content was turned off in <paramref name="target"/>. Returns whether the panel must be hidden again.</summary>
    public bool ContentOff(object target)
    {
        if (!ReferenceEquals(Revealed, target))
        {
            return false;
        }

        Revealed = null;
        return true;
    }

    /// <summary>The layout was changed some other way, or the revealed panel lost its content another way.</summary>
    public void Forget() => Revealed = null;
}
