namespace HabilisTerminal.Core.Panels;

/// <summary>Which panel is active, and whether it takes keyboard focus, after the panels' visibility changes (issue #35).</summary>
public static class PanelVisibilityRule
{
    /// <summary>
    /// A shown panel is always active. A panel that stays visible keeps priority over one that just appeared, the active
    /// one first; with no panel shown, the active one stays as it is (it comes back first).
    /// </summary>
    public static bool ActiveIsLeft(bool activeIsLeft, (bool Left, bool Right) before, (bool Left, bool Right) after)
    {
        if (!after.Left && !after.Right)
        {
            return activeIsLeft;
        }

        bool Stays(bool left) => left ? before.Left && after.Left : before.Right && after.Right;
        bool Shown(bool left) => left ? after.Left : after.Right;
        if (Stays(activeIsLeft))
        {
            return activeIsLeft;
        }

        if (Stays(!activeIsLeft))
        {
            return !activeIsLeft;
        }

        return Shown(activeIsLeft) ? activeIsLeft : !activeIsLeft;
    }

    /// <summary>
    /// Whether the active panel takes keyboard focus. Layout keys typed at the prompt keep the terminal focused, except
    /// when the panels reappear or a second panel joins (Far: two panels shown means one of them has focus).
    /// </summary>
    public static bool FocusesPanel((bool Left, bool Right) before, (bool Left, bool Right) after, bool terminalHasFocus)
    {
        if (!after.Left && !after.Right)
        {
            return false;
        }

        var none = !before.Left && !before.Right;
        var secondJoined = after.Left && after.Right && !(before.Left && before.Right);
        return none || secondJoined || !terminalHasFocus;
    }
}
