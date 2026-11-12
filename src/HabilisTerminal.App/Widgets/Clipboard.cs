#if WINDOWS
using System.Runtime.InteropServices;

namespace HabilisTerminal.App.Widgets;

/// <summary>
/// Text on the Windows clipboard, synchronously, as WPF's <c>Clipboard</c> gave it: Avalonia's clipboard is asynchronous,
/// and the callers here (copy, paste with a warning first) are not (issue #253). Throws
/// <see cref="ExternalException"/> while another program holds the clipboard, as WPF did.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201", Justification = "The callers catch what WPF's clipboard threw.")]
internal static class Win32Clipboard
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static bool ContainsText() => IsClipboardFormatAvailable(CF_UNICODETEXT);

    public static string GetText()
    {
        Open();
        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
            {
                return string.Empty;
            }

            var pointer = GlobalLock(handle);
            try
            {
                return Marshal.PtrToStringUni(pointer) ?? string.Empty;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static void SetText(string text)
    {
        Open();
        try
        {
            EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes);
            var pointer = GlobalLock(handle);
            Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
            Marshal.WriteInt16(pointer, text.Length * 2, 0);
            GlobalUnlock(handle);
            if (SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                throw new ExternalException("The clipboard did not take the text.");
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void Open()
    {
        // Another program may hold it for a moment.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return;
            }

            Thread.Sleep(10);
        }

        throw new ExternalException("The clipboard is in use by another program.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);
}
#endif
