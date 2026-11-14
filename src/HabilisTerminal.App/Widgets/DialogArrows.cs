using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace HabilisTerminal.App.Widgets;

/// <summary>
/// Arrow keys in dialogs as in Far (issue #294): Left and Right go from button to button in a row (round), Up and Down
/// go to the nearest control on the line above or below. A text box keeps Left and Right for its cursor, and a
/// multi-line one, a list or a tree keeps Up and Down too.
/// </summary>
internal static class DialogArrows
{
    /// <summary>True when the key moved the focus (or stayed on the only button), so the dialog takes it.</summary>
    public static bool Move(TopLevel root, Key key)
    {
        if (root.FocusManager?.GetFocusedElement() is not Control focused)
        {
            return false;
        }

        return key switch
        {
            Key.Left or Key.Right when focused is Button and not ToggleButton => AmongButtons((Button)focused, key == Key.Right ? 1 : -1),
            Key.Up or Key.Down when MovesVertically(focused) => ToNextControl(root, focused, key == Key.Down),
            _ => false,
        };
    }

    /// <summary>Controls where Up and Down mean nothing else: buttons, check boxes, radio buttons, one-line text boxes.</summary>
    internal static bool MovesVertically(Control control) => control is Button || control is TextBox { AcceptsReturn: false };

    private static bool AmongButtons(Button button, int direction)
    {
        if (button.Parent is not Panel row)
        {
            return false;
        }

        var buttons = row.Children.OfType<Button>().Where(b => b is not ToggleButton && b.IsVisible && b.IsEffectivelyEnabled && b.Focusable).ToList();
        var index = buttons.IndexOf(button);
        if (index < 0)
        {
            return false;
        }

        buttons[(index + direction + buttons.Count) % buttons.Count].Focus(NavigationMethod.Directional);
        return true;
    }

    /// <summary>
    /// The nearest control on a line above or below, as in Far: from a button row Up goes to the field over it, from a
    /// field Down to the row under it (the button nearest across). Nothing further at the top or bottom.
    /// </summary>
    private static bool ToNextControl(TopLevel root, Control focused, bool down)
    {
        if (Bounds(root, focused) is not { } from)
        {
            return false;
        }

        var best = root.GetVisualDescendants().OfType<InputElement>()
            .Where(c => c != focused && c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled && KeyboardNavigation.GetIsTabStop(c))
            .Select(c => (Control: c, Rect: Bounds(root, c)))
            .Where(c => c.Rect is { } r && (down ? r.Top >= from.Bottom - 1 : r.Bottom <= from.Top + 1))
            .OrderBy(c => Math.Abs(c.Rect!.Value.Center.Y - from.Center.Y))
            .ThenBy(c => Math.Abs(c.Rect!.Value.Center.X - from.Center.X))
            .Select(c => c.Control)
            .FirstOrDefault();
        if (best is null)
        {
            return false;
        }

        best.Focus(NavigationMethod.Tab);
        if (best is TextBox text)
        {
            text.SelectAll();
        }

        return true;
    }

    private static Rect? Bounds(TopLevel root, Visual control) =>
        control.TransformToVisual(root) is { } transform ? new Rect(control.Bounds.Size).TransformToAABB(transform) : null;
}
