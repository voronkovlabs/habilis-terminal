namespace HabilisTerminal.App;

/// <summary>A full-window page that puts keyboard focus on its own content when shown.</summary>
public interface IPage
{
    void FocusContent();
}

/// <summary>A page that may hold unsaved changes: asked before the window or the page closes.</summary>
public interface ILeavePage
{
    /// <summary>False means stay on the page.</summary>
    bool ConfirmLeave();
}
