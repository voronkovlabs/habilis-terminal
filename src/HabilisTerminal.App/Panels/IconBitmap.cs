#if WINDOWS
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace HabilisTerminal.App.Panels;

/// <summary>
/// A Windows icon handle as an Avalonia bitmap (issue #253; WPF had <c>Imaging.CreateBitmapSourceFromHIcon</c>): the icon is
/// drawn into a 32-bit top-down DIB and its pixels are copied. Safe off the UI thread.
/// </summary>
internal static class IconBitmap
{
    public static Bitmap? FromHIcon(IntPtr icon, int size)
    {
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        _ = ReleaseDC(IntPtr.Zero, screen);
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var dib = CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero)
        {
            DeleteDC(dc);
            return null;
        }

        var old = SelectObject(dc, dib);
        try
        {
            if (!DrawIconEx(dc, 0, 0, icon, size, size, 0, IntPtr.Zero, DI_NORMAL))
            {
                return null;
            }

            // DrawIconEx leaves alpha 0 for icons without an alpha channel: those are opaque where drawn.
            var pixels = new int[size * size];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            if (pixels.All(p => (p & unchecked((int)0xFF000000)) == 0))
            {
                for (var i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i] != 0)
                    {
                        pixels[i] |= unchecked((int)0xFF000000);
                    }
                }

                Marshal.Copy(pixels, 0, bits, pixels.Length);
            }

            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, bits, new PixelSize(size, size), new Vector(96, 96), size * 4);
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(dib);
            DeleteDC(dc);
        }
    }

    private const uint DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
}
#endif
