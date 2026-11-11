using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using HabilisTerminal.Core.Settings;

namespace HabilisTerminal.App.Widgets;

/// <summary>
/// The interface zoom (issue #237): one <see cref="ScaleTransform"/> shared by the layout transforms that hold the main
/// window's content, the dialogs' content and our popups. Avalonia has no layout transform on every control, so content
/// is wrapped in a <see cref="LayoutTransformControl"/> (issue #253).
/// </summary>
public static class UiZoom
{
    private static readonly ScaleTransform Scale = new(1, 1);

    /// <summary>After every change, on the UI thread.</summary>
    public static event Action? Changed;

    /// <summary>1 for 100%.</summary>
    public static double Factor { get; private set; } = 1.0;

    /// <summary>Sets the zoom, kept within <see cref="ZoomSteps"/>' range. False when nothing changed.</summary>
    public static bool Set(double factor)
    {
        factor = ZoomSteps.Clamp(factor);
        if (Math.Abs(factor - Factor) < 0.0005)
        {
            return false;
        }

        Factor = factor;
        Scale.ScaleX = factor;
        Scale.ScaleY = factor;
        Changed?.Invoke();
        return true;
    }

    /// <summary>One step up or down, as Ctrl+wheel and Ctrl+Alt+= / − do.</summary>
    public static bool Step(int direction) => Set(ZoomSteps.Next(Factor, direction));

    public static bool Reset() => Set(1.0);

    /// <summary>
    /// WPF kept a bitmap drawn for the zoomed pixels out of the zoom; Avalonia's layout transform scales it with the rest
    /// for now (issue #253): a PDF page may be a little soft at zooms other than 100%.
    /// </summary>
    public static void Unscale(Control element)
    {
    }

    /// <summary>Was a pixel snap for unscaled bitmaps under WPF; Avalonia's layout rounding covers it (issue #253).</summary>
    public static void SnapToPixels(Control element)
    {
    }

    /// <summary>The content, scaled with the interface.</summary>
    public static LayoutTransformControl Wrap(Control content) => new() { LayoutTransform = Scale, Child = content };

    /// <summary>A popup's content, scaled; the popup's own offsets are the caller's.</summary>
    public static void Apply(Popup popup)
    {
        if (popup.Child is Control child and not LayoutTransformControl)
        {
            popup.Child = null;
            popup.Child = Wrap(child);
        }
    }

    /// <summary>
    /// A window of ours: its content scaled, Ctrl+wheel anywhere in it zooms. The content may be set after this call.
    /// </summary>
    public static void Attach(Window window)
    {
        void ApplyContent()
        {
            if (window.Content is Control content and not LayoutTransformControl)
            {
                window.Content = null;
                window.Content = Wrap(content);
            }
        }

        ApplyContent();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == ContentControl.ContentProperty)
            {
                ApplyContent();
            }
        };
        window.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.Control && e.Delta.Y != 0)
            {
                e.Handled = true;
                Step(Math.Sign(e.Delta.Y));
            }
        }, RoutingStrategies.Tunnel);
    }
}
