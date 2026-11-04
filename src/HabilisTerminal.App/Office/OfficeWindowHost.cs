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

    private IntPtr _container;
    private IntPtr _docked;

    /// <summary>Takes <paramref name="window"/> out of its frame and fits it here.</summary>
    public void Dock(IntPtr window)
    {
        _docked = window;
        ShowWindow(window, SW_RESTORE);
        var style = GetWindowLongPtr(window, GWL_STYLE).ToInt64();
        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        SetWindowLongPtr(window, GWL_STYLE, new IntPtr(style | WS_CHILD));
        var extended = GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(window, GWL_EXSTYLE, new IntPtr(extended & ~WS_EX_APPWINDOW));
        SetParent(window, _container);
        Fit(SWP_FRAMECHANGED | SWP_SHOWWINDOW);
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

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
