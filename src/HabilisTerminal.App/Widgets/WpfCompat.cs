using Avalonia;
using Avalonia.Input;

namespace HabilisTerminal.App;

/// <summary>
/// The WPF names the code-built UI used everywhere, on Avalonia (issue #253), so the port changes behaviour, not
/// spelling: sizes after layout, focus and pointer state, the press's button.
/// </summary>
internal static class WpfCompat
{
    extension(Visual visual)
    {
        /// <summary>The width after layout (WPF's ActualWidth).</summary>
        public double ActualWidth => visual.Bounds.Width;

        /// <summary>The height after layout (WPF's ActualHeight).</summary>
        public double ActualHeight => visual.Bounds.Height;

        /// <summary>The size after layout (WPF's RenderSize).</summary>
        public Size RenderSize => visual.Bounds.Size;
    }

    extension(InputElement element)
    {
        /// <summary>WPF's IsKeyboardFocused: the element itself has the focus.</summary>
        public bool IsKeyboardFocused => element.IsFocused;

        /// <summary>WPF's IsMouseOver.</summary>
        public bool IsMouseOver => element.IsPointerOver;
    }

    extension(Avalonia.Controls.Control control)
    {
        /// <summary>
        /// WPF's Visibility: Hidden keeps the element's place and size (a console behind a page keeps its grid), so it
        /// is drawn transparent and lets the pointer through instead of leaving the layout.
        /// </summary>
        public Visibility Visibility
        {
            get => !control.IsVisible ? Visibility.Collapsed : control.Opacity == 0 && !control.IsHitTestVisible ? Visibility.Hidden : Visibility.Visible;
            set
            {
                control.IsVisible = value != Visibility.Collapsed;
                control.Opacity = value == Visibility.Hidden ? 0 : 1;
                control.IsHitTestVisible = value != Visibility.Hidden;
            }
        }

        /// <summary>WPF's ToolTip property.</summary>
        public object? ToolTip
        {
            get => Avalonia.Controls.ToolTip.GetTip(control);
            set => Avalonia.Controls.ToolTip.SetTip(control, value);
        }
    }

    extension(Size size)
    {
        /// <summary>WPF's Size.Empty, here (0, 0): a size not known yet.</summary>
        public static Size Empty => default;

        public bool IsEmpty => size.Width <= 0 && size.Height <= 0;
    }

    extension(Rect rect)
    {
        public bool IsEmpty => rect.Width <= 0 && rect.Height <= 0;
    }

    extension(Avalonia.Media.Imaging.Bitmap bitmap)
    {
        public int PixelWidth => bitmap.PixelSize.Width;

        public int PixelHeight => bitmap.PixelSize.Height;
    }

    extension(PointerPressedEventArgs e)
    {
        /// <summary>The button that was pressed (WPF's ChangedButton).</summary>
        public MouseButton ChangedButton => e.GetCurrentPoint(null).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.RightButtonPressed => MouseButton.Right,
            PointerUpdateKind.MiddleButtonPressed => MouseButton.Middle,
            PointerUpdateKind.XButton1Pressed => MouseButton.XButton1,
            PointerUpdateKind.XButton2Pressed => MouseButton.XButton2,
            _ => MouseButton.Left,
        };
    }

    extension(PointerReleasedEventArgs e)
    {
        /// <summary>The button that was released (WPF's ChangedButton).</summary>
        public MouseButton ChangedButton => e.InitialPressMouseButton;
    }
}

/// <summary>WPF's visibility states (see <see cref="WpfCompat"/>).</summary>
internal enum Visibility
{
    Visible,
    Hidden,
    Collapsed,
}

/// <summary>WPF's <c>Cursors</c> on Avalonia.</summary>
internal static class Cursors
{
    public static readonly Cursor Arrow = new(StandardCursorType.Arrow);
    public static readonly Cursor IBeam = new(StandardCursorType.Ibeam);
    public static readonly Cursor Hand = new(StandardCursorType.Hand);
    public static readonly Cursor SizeWE = new(StandardCursorType.SizeWestEast);
    public static readonly Cursor SizeNS = new(StandardCursorType.SizeNorthSouth);
    public static readonly Cursor Wait = new(StandardCursorType.Wait);
}
