using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using HabilisTerminal.App.Widgets;

namespace HabilisTerminal.App.Viewing;

/// <summary>
/// The view's own zoom for a picture or a PDF page (issue #241), as in Windows Photos: fitted at first, Ctrl+wheel zooms
/// around the pointer, a zoomed picture is dragged or scrolled, double click fits it again. 100% is one content pixel
/// per screen pixel whatever the interface zoom, and the picture lands on whole screen pixels so it stays sharp.
/// </summary>
public sealed class ZoomPanView : Canvas
{
    private const double Step = 1.25;
    private const double MaxScale = 32;

    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private Size _pixels;
    private bool _fit = true;
    private double _scale = 1;
    private Point _offset;
    private Point? _dragFrom;

    public ZoomPanView(bool enlargeToFit)
    {
        AddHandler(PointerWheelChangedEvent, OnPreviewMouseWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        EnlargeToFit = enlargeToFit;
        ClipToBounds = true;
        Background = Brushes.Transparent;
        Focusable = false;
        RenderOptions.SetBitmapInterpolationMode(_image, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        Children.Add(_image);
        Loaded += (_, _) => UiZoom.Changed += Relayout;
        Unloaded += (_, _) => UiZoom.Changed -= Relayout;
    }

    /// <summary>After a zoom, a pan or a resize: the header's percentage, a sharper PDF render.</summary>
    public event Action? ViewChanged;

    /// <summary>A PDF page fills the window; a picture is never enlarged just to fit.</summary>
    public bool EnlargeToFit { get; }

    public bool IsFitted => _fit;

    /// <summary>The content as it is drawn now, in screen pixels.</summary>
    public Size DisplayPixels => new(_pixels.Width * Scale, _pixels.Height * Scale);

    /// <summary>"Fit 42%" or "200%".</summary>
    public string PercentText
    {
        get
        {
            var percent = ((int)Math.Round(Scale * 100)).ToString(CultureInfo.InvariantCulture) + "%";
            return _fit ? "Fit " + percent : percent;
        }
    }

    /// <summary>Screen pixels per content pixel.</summary>
    private double Scale => _fit ? FitScale : _scale;

    private double FitScale
    {
        get
        {
            var view = ViewPixels;
            if (_pixels.Width < 1 || _pixels.Height < 1 || view.Width < 1 || view.Height < 1)
            {
                return 1;
            }

            var fit = Math.Min(view.Width / _pixels.Width, view.Height / _pixels.Height);
            return EnlargeToFit ? fit : Math.Min(1, fit);
        }
    }

    /// <summary>Screen pixels per local unit: the monitor's scale and the interface zoom.</summary>
    private double PixelsPerUnit => (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1) * UiZoom.Factor;

    private Size ViewPixels => new(Bounds.Width * PixelsPerUnit, Bounds.Height * PixelsPerUnit);

    /// <summary>What content of <paramref name="pixels"/> size would take on screen when fitted, in screen pixels.</summary>
    public Size FittedPixels(Size pixels)
    {
        var view = ViewPixels;
        if (pixels.Width < 1 || pixels.Height < 1 || view.Width < 1 || view.Height < 1)
        {
            return pixels;
        }

        var fit = Math.Min(view.Width / pixels.Width, view.Height / pixels.Height);
        fit = EnlargeToFit ? fit : Math.Min(1, fit);
        return new Size(pixels.Width * fit, pixels.Height * fit);
    }

    /// <summary>New content of <paramref name="pixels"/> size, fitted.</summary>
    public void Show(IImage? source, Size pixels)
    {
        _image.Source = source;
        _pixels = pixels;
        _fit = true;
        Relayout();
    }

    /// <summary>The same content drawn at another resolution (a PDF page for the zoom); the view stays.</summary>
    public void Replace(IImage source) => _image.Source = source;

    public void Fit()
    {
        _fit = true;
        Relayout();
    }

    /// <summary>One step in or out around <paramref name="at"/> (local units); zooming out past the fit fits.</summary>
    public void ZoomAt(Point at, int direction)
    {
        if (_pixels.Width < 1 || direction == 0)
        {
            return;
        }

        var old = Scale;
        var next = direction > 0 ? old * Step : old / Step;
        if (direction < 0 && next <= FitScale)
        {
            Fit();
            return;
        }

        next = Math.Min(next, MaxScale);
        var ratio = next / old;
        var current = _fit ? CenteredOffset(old) : _offset;
        _offset = new Point(at.X - ((at.X - current.X) * ratio), at.Y - ((at.Y - current.Y) * ratio));
        _scale = next;
        _fit = false;
        Relayout();
    }

    /// <summary>Ctrl+Alt+= / − from the keyboard: around the middle.</summary>
    public void ZoomCenter(int direction) => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), direction);

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Relayout();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount == 2)
        {
            // Fitted: to 100% at the pointer; zoomed: fitted again.
            if (_fit && Math.Abs(FitScale - 1) > 0.01)
            {
                var old = FitScale;
                var at = e.GetPosition(this);
                var current = CenteredOffset(old);
                _offset = new Point(at.X - ((at.X - current.X) / old), at.Y - ((at.Y - current.Y) / old));
                _scale = 1;
                _fit = false;
                Relayout();
            }
            else
            {
                Fit();
            }

            e.Handled = true;
            return;
        }

        if (!_fit)
        {
            _dragFrom = e.GetPosition(this);
            e.Pointer.Capture(this);
            Cursor = Cursors.Hand;
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is { } from && IsPointerOver)
        {
            var now = e.GetPosition(this);
            _offset += now - from;
            _dragFrom = now;
            Relayout();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragFrom is not null)
        {
            _dragFrom = null;
            e.Pointer.Capture(null);
            Cursor = null;
        }
    }

    /// <summary>Zoomed, the wheel scrolls (Shift+wheel sideways); fitted, it is left to the page.</summary>
    private void OnPreviewMouseWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_fit || Keyboard.Modifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        // A notch moves a quarter of the view; a trackpad moves it by as much as it was moved (#275).
        var step = e.Delta.Y * Bounds.Height / 4;
        _offset = Keyboard.Modifiers.HasFlag(KeyModifiers.Shift) ? new Point(_offset.X + step, _offset.Y) : new Point(_offset.X, _offset.Y + step);
        Relayout();
        e.Handled = true;
    }

    private Point CenteredOffset(double scale)
    {
        var size = ContentSize(scale);
        return new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2);
    }

    private Size ContentSize(double scale)
    {
        var unit = PixelsPerUnit;
        return new Size(_pixels.Width * scale / unit, _pixels.Height * scale / unit);
    }

    private void Relayout()
    {
        if (_pixels.Width < 1 || Bounds.Width < 1)
        {
            _image.Width = _image.Height = 0;
            ViewChanged?.Invoke();
            return;
        }

        var unit = PixelsPerUnit;
        var scale = Scale;

        // Whole screen pixels for the size and the place: a half pixel blurs every line.
        double Round(double local) => Math.Round(local * unit) / unit;
        var size = new Size(Round(_pixels.Width * scale / unit), Round(_pixels.Height * scale / unit));
        double Place(double offset, double content, double view) =>
            content <= view ? (view - content) / 2 : Math.Clamp(offset, view - content, 0);

        if (_fit)
        {
            _offset = new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2);
        }

        _offset = new Point(Place(_offset.X, size.Width, Bounds.Width), Place(_offset.Y, size.Height, Bounds.Height));
        _image.Width = size.Width;
        _image.Height = size.Height;

        // The canvas itself may sit between pixels under the interface zoom.
        var origin = TopLevel.GetTopLevel(this) is { } root ? this.TranslatePoint(default, root) ?? default : default;
        var dpi = (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        double Snap(double local, double originUnits) => (Math.Round((originUnits * dpi) + (local * unit)) - (originUnits * dpi)) / unit;
        SetLeft(_image, Snap(_offset.X, origin.X));
        SetTop(_image, Snap(_offset.Y, origin.Y));
        ViewChanged?.Invoke();
    }
}
