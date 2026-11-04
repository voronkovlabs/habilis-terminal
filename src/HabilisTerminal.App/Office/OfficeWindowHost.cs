using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HabilisTerminal.App.Office;

/// <summary>
/// Holds an Office document window as a child of the app's window (issue #101). Office does not support this: the window
/// is taken from its frame with <c>SetParent</c>, because Microsoft removed real document embedding.
/// </summary>
public sealed class OfficeWindowHost : HwndHost
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_CHILD = 0x40000000;
    private const long WS_VISIBLE = 0x10000000;
    private const long WS_CLIPCHILDREN = 0x02000000;
    private const long WS_POPUP = 0x80000000;
    private const long WS_CAPTION = 0x00C00000;
    private const long WS_THICKFRAME = 0x00040000;
    private const long WS_MINIMIZEBOX = 0x00020000;
    private const long WS_MAXIMIZEBOX = 0x00010000;
    private const long WS_SYSMENU = 0x00080000;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const int SW_RESTORE = 9;
    private const int SW_HIDE = 0;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ERASE = 0x0004;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_FRAME = 0x0400;

    private const int WM_PAINT = 0x000F;
    private const int WM_ACTIVATE = 0x0006;
    private const int WM_NCACTIVATE = 0x0086;
    private const int WA_ACTIVE = 1;

    private readonly System.Windows.Media.Color _color;
    private IntPtr _container;
    private IntPtr _docked;
    private readonly Dictionary<IntPtr, IntPtr> _styles = [];
    private IntPtr _background;

    public OfficeWindowHost(System.Windows.Media.Color background) => _color = background;

    /// <summary>Takes <paramref name="window"/> out of its frame and fits it here.</summary>
    public void Dock(IntPtr window)
    {
        if (_docked == window)
        {
            return;
        }

        // Excel replaces its first window when a workbook opens: the old one stays a hidden child until Settle.
        if (_docked != IntPtr.Zero)
        {
            ShowWindow(_docked, SW_HIDE);
        }

        _docked = window;
        _styles[window] = GetWindowLongPtr(window, GWL_STYLE);

        // A child first: restoring the window on the desktop would bring it on the screen.
        var style = GetWindowLongPtr(window, GWL_STYLE).ToInt64();
        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        SetWindowLongPtr(window, GWL_STYLE, new IntPtr(style | WS_CHILD));
        var extended = GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(window, GWL_EXSTYLE, new IntPtr(extended & ~WS_EX_APPWINDOW));
        SetParent(window, _container);
        ShowWindow(window, SW_RESTORE);
        Fit(SWP_FRAMECHANGED | SWP_SHOWWINDOW);
    }

    /// <summary>Left, top, width and height of this host on the screen.</summary>
    public int[] ScreenArea() =>
        GetWindowRect(Handle, out var area) ? [area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top] : [0, 0, 1, 1];

    /// <summary>
    /// The document's window is known now. A window taken by mistake while Office was starting, one the user opened in
    /// another Excel at that moment, goes back to the desktop with its frame.
    /// </summary>
    public void Settle(IntPtr document)
    {
        _ = GetWindowThreadProcessId(document, out var office);
        foreach (var (window, style) in _styles)
        {
            if (window != document && IsWindow(window) && GetWindowThreadProcessId(window, out var process) != 0 && process != office)
            {
                SetParent(window, IntPtr.Zero);
                SetWindowLongPtr(window, GWL_STYLE, style);
                ShowWindow(window, SW_RESTORE);
            }
        }

        _styles.Clear();
    }

    /// <summary>Back out of the app's window, hidden: closing it must not depend on the app's thread.</summary>
    public void Undock()
    {
        if (_docked != IntPtr.Zero)
        {
            ShowWindow(_docked, SW_HIDE);
            SetParent(_docked, IntPtr.Zero);
            _docked = IntPtr.Zero;
        }
    }

    /// <summary>
    /// A resize and a repaint: Excel lays out and draws its ribbon only then, after it stops being transparent. A size
    /// that does not change would not reach it.
    /// </summary>
    public void Refresh()
    {
        if (_docked != IntPtr.Zero && GetClientRect(_container, out var client))
        {
            SetWindowPos(_docked, IntPtr.Zero, 0, 0, Math.Max(1, client.Right - 1), Math.Max(1, client.Bottom - 1), SWP_NOZORDER);
            SetWindowPos(_docked, IntPtr.Zero, 0, 0, client.Right, client.Bottom, SWP_NOZORDER | SWP_FRAMECHANGED);
            RedrawWindow(_docked, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_ERASE | RDW_FRAME);
        }
    }

    /// <summary>A child gets no activation of its own, and Office draws parts of its frame only when active.</summary>
    public void Activate()
    {
        if (_docked != IntPtr.Zero)
        {
            SendMessage(_docked, WM_ACTIVATE, new IntPtr(WA_ACTIVE), IntPtr.Zero);
            SendMessage(_docked, WM_NCACTIVATE, new IntPtr(1), IntPtr.Zero);
        }
    }

    public void FocusDocked()
    {
        if (_docked != IntPtr.Zero)
        {
            SetFocus(_docked);
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _container = CreateWindowEx(0, "STATIC", string.Empty, unchecked((uint)(WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN)), 0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, _container);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Undock();
        DestroyWindow(hwnd.Handle);
        if (_background != IntPtr.Zero)
        {
            DeleteObject(_background);
        }
    }

    /// <summary>The theme's colour under the document, instead of the system's light one while Office lays itself out.</summary>
    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_PAINT)
        {
            var dc = BeginPaint(hwnd, out var paint);
            if (GetClientRect(hwnd, out var client))
            {
                if (_background == IntPtr.Zero)
                {
                    _background = CreateSolidBrush((uint)(_color.R | (_color.G << 8) | (_color.B << 16)));
                }

                _ = FillRect(dc, ref client, _background);
            }

            EndPaint(hwnd, ref paint);
            handled = true;
            return IntPtr.Zero;
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        Fit(0);
    }

    private void Fit(uint flags)
    {
        if (_docked != IntPtr.Zero && GetClientRect(_container, out var client))
        {
            SetWindowPos(_docked, IntPtr.Zero, 0, 0, client.Right, client.Bottom, SWP_NOZORDER | flags);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr Hdc;
        public int Erase;
        public NativeRect Paint;
        public int Restore;
        public int IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Reserved;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr window, out PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr window, ref PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr dc, ref NativeRect rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}
