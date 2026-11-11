using System.Windows;
using System.Windows.Input;
using System.Windows.Shell;
using HabilisTerminal.App.Input;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Settings;

namespace HabilisTerminal.App;

/// <summary>
/// The interface zoom (issue #237): Ctrl+wheel anywhere and Ctrl+Alt+= / − / 0. The title bar shows the percentage while
/// it is not 100% (issue #239).
/// </summary>
public partial class MainWindow
{
    private void InstallZoom(AppState state)
    {
        UiZoom.Set(state.Zoom ?? 1.0);
        UiZoom.Apply(Root);
        ApplyCaptionHeight();
        UiZoom.Changed += OnZoomChanged;
        PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Delta != 0)
            {
                e.Handled = true;
                UiZoom.Step(Math.Sign(e.Delta));
            }
        };

        // A WebView2 page holds the keyboard in its own process: its Ctrl+Alt keys come back only as routed events.
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var modifiers = (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? KeyModifiers.Control : KeyModifiers.None)
                | (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? KeyModifiers.Alt : KeyModifiers.None)
                | (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? KeyModifiers.Shift : KeyModifiers.None);
            if (HandleZoomKey(key, modifiers))
            {
                e.Handled = true;
            }
        };
    }

    /// <summary>Ctrl+Alt+= / Ctrl+Alt+− / Ctrl+Alt+0, from any focus; the gray keys too.</summary>
    private static bool HandleZoomKey(Key key, KeyModifiers modifiers)
    {
        if (modifiers != (KeyModifiers.Control | KeyModifiers.Alt))
        {
            return false;
        }

        switch (key)
        {
            case Key.OemPlus or Key.Add:
                UiZoom.Step(1);
                return true;
            case Key.OemMinus or Key.Subtract:
                UiZoom.Step(-1);
                return true;
            case Key.D0 or Key.NumPad0:
                UiZoom.Reset();
                return true;
            default:
                return false;
        }
    }

    private void OnZoomChanged()
    {
        ApplyCaptionHeight();
        foreach (var tab in _tabs)
        {
            UiZoom.Unscale(tab.Terminal);
        }

        ApplyTerminalTheme(_themes.Current);
        UpdateCellSize();
    }

    /// <summary>The title bar grows with the zoom, so its drag area must too.</summary>
    private void ApplyCaptionHeight()
    {
        if (WindowChrome.GetWindowChrome(this) is { IsFrozen: false } chrome)
        {
            chrome.CaptionHeight = TitleBar.BarHeight * UiZoom.Factor;
        }
    }
}
