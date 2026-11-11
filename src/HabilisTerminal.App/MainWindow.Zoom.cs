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

                // A picture or a PDF page zooms itself (issue #241).
                if (ContentZoom is { } view)
                {
                    view.ZoomAt(e.GetPosition(view), Math.Sign(e.Delta));
                }
                else
                {
                    UiZoom.Step(Math.Sign(e.Delta));
                }
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

    /// <summary>The open page's own zoom (a picture, a PDF page), if it has one.</summary>
    private Viewing.ZoomPanView? ContentZoom => IsPageOpen && PageHost.Content is Viewing.IContentZoom page ? page.ZoomView : null;

    /// <summary>Ctrl+Alt+= / Ctrl+Alt+− / Ctrl+Alt+0, from any focus; the gray keys too. A picture zooms itself.</summary>
    private bool HandleZoomKey(Key key, KeyModifiers modifiers)
    {
        if (modifiers != (KeyModifiers.Control | KeyModifiers.Alt))
        {
            return false;
        }

        var view = ContentZoom;
        switch (key)
        {
            case Key.OemPlus or Key.Add:
                Step(1);
                return true;
            case Key.OemMinus or Key.Subtract:
                Step(-1);
                return true;
            case Key.D0 or Key.NumPad0 when view is not null:
                view.Fit();
                return true;
            case Key.D0 or Key.NumPad0:
                UiZoom.Reset();
                return true;
            default:
                return false;
        }

        void Step(int direction)
        {
            if (view is not null)
            {
                view.ZoomCenter(direction);
            }
            else
            {
                UiZoom.Step(direction);
            }
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
