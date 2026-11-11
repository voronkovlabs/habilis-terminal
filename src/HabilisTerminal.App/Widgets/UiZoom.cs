using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HabilisTerminal.Core.Settings;
using Microsoft.Web.WebView2.Wpf;

namespace HabilisTerminal.App.Widgets;

/// <summary>
/// The interface zoom (issue #237): one <see cref="ScaleTransform"/> shared as the layout transform of the main window's
/// content, the dialogs' content, our popups and, through class handlers, every tooltip and context menu. Native parts
/// follow <see cref="Changed"/>: the console's font size, WebView2's zoom factor.
/// </summary>
public static class UiZoom
{
    private static readonly ScaleTransform Scale = new(1, 1);

    static UiZoom()
    {
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((s, _) => Apply((FrameworkElement)s)));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) => Apply((FrameworkElement)s)));
    }

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

    /// <summary>Scales this element and what it holds.</summary>
    public static void Apply(FrameworkElement element) => element.LayoutTransform = Scale;

    /// <summary>
    /// A bitmap drawn for the zoomed pixels (a PDF page) is kept out of the zoom, else it is filtered twice and blurs:
    /// the element gets the inverse scale. Call again after <see cref="Changed"/>.
    /// </summary>
    public static void Unscale(FrameworkElement element) =>
        element.LayoutTransform = Math.Abs(Factor - 1) < 0.0005 ? Transform.Identity : new ScaleTransform(1 / Factor, 1 / Factor);

    /// <summary>
    /// Keeps an unscaled bitmap on whole screen pixels: under the zoom, layout rounding no longer lands on them, and a
    /// half-pixel offset blurs every line. A render transform moves the element by the remainder after each layout.
    /// </summary>
    public static void SnapToPixels(FrameworkElement element)
    {
        element.LayoutUpdated += (_, _) =>
        {
            if (PresentationSource.FromVisual(element)?.RootVisual is not { } root || !element.IsDescendantOf(root))
            {
                return;
            }

            var toRoot = element.TransformToAncestor(root);
            var origin = toRoot.Transform(default);
            var scale = toRoot.Transform(new Point(1, 1)) - origin;
            if (scale.X <= 0 || scale.Y <= 0)
            {
                return;
            }

            var current = element.RenderTransform as TranslateTransform;
            var dpi = VisualTreeHelper.GetDpi(element);
            var x = (origin.X - ((current?.X ?? 0) * scale.X)) * dpi.DpiScaleX;
            var y = (origin.Y - ((current?.Y ?? 0) * scale.Y)) * dpi.DpiScaleY;
            var dx = (Math.Round(x) - x) / (dpi.DpiScaleX * scale.X);
            var dy = (Math.Round(y) - y) / (dpi.DpiScaleY * scale.Y);
            if (Math.Abs(dx - (current?.X ?? 0)) > 0.001 || Math.Abs(dy - (current?.Y ?? 0)) > 0.001)
            {
                element.RenderTransform = new TranslateTransform(dx, dy);
            }
        };
    }

    /// <summary>A popup's content, scaled; the popup's own offsets are the caller's.</summary>
    public static void Apply(Popup popup)
    {
        if (popup.Child is FrameworkElement child)
        {
            Apply(child);
        }
    }

    /// <summary>
    /// A WebView2 is a native window, which the transform only sizes: its page takes the zoom as its zoom factor, and
    /// Ctrl+wheel inside it, which WPF never sees, zooms the whole interface. The returned action stops following.
    /// </summary>
    public static Action Follow(WebView2 web)
    {
        void FromZoom() => web.ZoomFactor = Factor;
        void FromPage(object? sender, EventArgs e) => Set(web.ZoomFactor);

        FromZoom();
        Changed += FromZoom;
        web.ZoomFactorChanged += FromPage;
        return () =>
        {
            Changed -= FromZoom;
            web.ZoomFactorChanged -= FromPage;
        };
    }

    /// <summary>
    /// A window of ours: its content scaled, Ctrl+wheel anywhere in it zooms. The content may be set after this call.
    /// </summary>
    public static void Attach(Window window)
    {
        void ApplyContent()
        {
            if (window.Content is FrameworkElement content)
            {
                Apply(content);
            }
        }

        ApplyContent();
        window.Loaded += (_, _) => ApplyContent();
        window.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Delta != 0)
            {
                e.Handled = true;
                Step(Math.Sign(e.Delta));
            }
        };
    }
}
