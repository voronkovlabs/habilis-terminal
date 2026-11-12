#if WINDOWS
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using HabilisTerminal.App.Hosting;
using HabilisTerminal.App.Input;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Settings;

namespace HabilisTerminal.App.Platform;

/// <summary>The desktop through Win32 and the Windows shell, as the app has always used it.</summary>
internal sealed class WindowsDesktop : IDesktop
{
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int KeyEventKeyUp = 2;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMAXIMIZED = 3;

    public bool ClipboardContainsText() => Win32Clipboard.ContainsText();

    public string ClipboardText() => Win32Clipboard.GetText();

    public void SetClipboardText(string text) => Win32Clipboard.SetText(text);

    public KeyModifiers HeldModifiers =>
        (Down(VK_MENU) ? KeyModifiers.Alt : KeyModifiers.None) |
        (Down(VK_CONTROL) ? KeyModifiers.Control : KeyModifiers.None) |
        (Down(VK_SHIFT) ? KeyModifiers.Shift : KeyModifiers.None);

    public char? CharOf(Key key, bool shift) => Win32KeyText.CharOf(key, shift);

    public Bitmap? FileIcon(string path, int size) => Win32FileIcons.Load(path, size);

    public bool ShowContextMenu(Window owner, string folder, IReadOnlyList<string> names, PixelPoint screen, Func<string, bool>? intercept) =>
        Win32ShellContextMenu.Show(owner, folder, names, screen, intercept);

    public void Eject(string root) => Win32ShellVerbs.Eject(root);

    public void SetDarkFrame(Window window, bool dark)
    {
        if (window.TryGetPlatformHandle()?.Handle is { } handle)
        {
            var useDarkMode = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
        }
    }

    /// <summary>
    /// <c>SetWindowPlacement</c> keeps the window on its monitor (re-scaled when that monitor's DPI differs), maximises
    /// it there, and moves it into view when that monitor is gone. Applied once the window handle exists.
    /// </summary>
    public void RestorePlacement(Window window, WindowPlacement placement) => window.Opened += (_, _) =>
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } handle)
        {
            return;
        }

        var native = new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>(),
            showCmd = placement.Maximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL,
            rcNormalPosition = new RECT
            {
                Left = (int)placement.Left,
                Top = (int)placement.Top,
                Right = (int)(placement.Left + placement.Width),
                Bottom = (int)(placement.Top + placement.Height),
            },
        };
        SetWindowPlacement(handle, ref native);
    };

    public WindowPlacement? CapturePlacement(Window window)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var native = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (handle == IntPtr.Zero || !GetWindowPlacement(handle, ref native))
        {
            return null;
        }

        var bounds = native.rcNormalPosition;
        return new WindowPlacement(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, WindowPlacement.IsMaximized(native.showCmd, native.flags));
    }

    /// <summary>Through the system's input queue, so the real keyboard state (and the page's own handling) applies.</summary>
    public void PressKey(Key key, KeyModifiers modifiers)
    {
        byte[] held =
        [
            .. modifiers.HasFlag(KeyModifiers.Control) ? new byte[] { VK_CONTROL } : [],
            .. modifiers.HasFlag(KeyModifiers.Shift) ? new byte[] { VK_SHIFT } : [],
            .. modifiers.HasFlag(KeyModifiers.Alt) ? new byte[] { VK_MENU } : [],
        ];
        foreach (var code in held)
        {
            keybd_event(code, 0, 0, IntPtr.Zero);
        }

        var virtualKey = (byte)(TerminalKeys.VirtualKey(key) ?? 0);
        keybd_event(virtualKey, 0, 0, IntPtr.Zero);
        keybd_event(virtualKey, 0, KeyEventKeyUp, IntPtr.Zero);
        foreach (var code in held.Reverse())
        {
            keybd_event(code, 0, KeyEventKeyUp, IntPtr.Zero);
        }
    }

    private static bool Down(int key) => (GetKeyState(key) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, int flags, IntPtr extra);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);
}
#endif
