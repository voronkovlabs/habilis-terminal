using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Input;
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
    private readonly WheelSteps _zoomWheel = new();

    private void InstallZoom(AppState state)
    {
        UiZoom.Set(state.Zoom ?? 1.0);
        Content = null;
        Content = UiZoom.Wrap(Root);
        ApplyCaptionHeight();
        UiZoom.Changed += OnZoomChanged;
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (Keyboard.Modifiers == KeyModifiers.Control && e.Delta.Y != 0)
            {
                e.Handled = true;

                // A picture or a PDF page zooms itself (issue #241); a trackpad's fractions add up to steps (#275).
                var steps = _zoomWheel.Take(e.Delta.Y);
                for (var i = 0; i < Math.Abs(steps); i++)
                {
                    if (ContentZoom is { } view)
                    {
                        view.ZoomAt(e.GetPosition(view), Math.Sign(steps));
                    }
                    else
                    {
                        UiZoom.Step(Math.Sign(steps));
                    }
                }
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // A Mac trackpad's pinch (#275): the interface zooms, or the picture or the PDF page in their views.
        AddHandler(PointerTouchPadGestureMagnifyEvent, (_, e) =>
        {
            e.Handled = true;
            var steps = _pinch.Take(e.Delta.X, 4); // about a quarter of magnification a step
            for (var i = 0; i < Math.Abs(steps); i++)
            {
                if (ContentZoom is { } view)
                {
                    view.ZoomAt(e.GetPosition(view), Math.Sign(steps));
                }
                else
                {
                    UiZoom.Step(Math.Sign(steps));
                }
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private readonly WheelSteps _pinch = new();

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

    /// <summary>The console is scaled with the rest (issue #253): its grid stays, only the title bar's height follows.</summary>
    private void OnZoomChanged() => ApplyCaptionHeight();

    /// <summary>The title bar grows with the zoom, so its drag area must too.</summary>
    private void ApplyCaptionHeight() => ExtendClientAreaTitleBarHeightHint = TitleBar.BarHeight * UiZoom.Factor;
}
