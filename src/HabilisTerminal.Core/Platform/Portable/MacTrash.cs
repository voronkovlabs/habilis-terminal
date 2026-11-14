using System.Runtime.InteropServices;

namespace HabilisTerminal.Core.Platform.Portable;

/// <summary>
/// The Trash as Finder uses it (issue #315): NSFileManager's trashItemAtURL, through the Objective-C runtime. It picks the
/// right trash for the volume, keeps what "Put Back" needs, and does not need access to ~/.Trash itself.
/// </summary>
internal static class MacTrash
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
    private static readonly Lazy<bool> Loaded = new(() => NativeLibrary.TryLoad(Foundation, out _));

    /// <summary>Moves the item to the Trash.</summary>
    /// <exception cref="IOException">macOS refused; the message is its own.</exception>
    public static void Trash(string path)
    {
        if (!Loaded.Value)
        {
            throw new IOException("The Foundation framework could not be loaded.");
        }

        var pool = Send(Send(Class("NSAutoreleasePool"), "alloc"), "init");
        try
        {
            var text = SendString(Class("NSString"), "stringWithUTF8String:", path);
            var url = SendPointer(Class("NSURL"), "fileURLWithPath:", text);
            var manager = Send(Class("NSFileManager"), "defaultManager");
            if (url == IntPtr.Zero || manager == IntPtr.Zero)
            {
                throw new IOException("The Trash is not available.");
            }

            if (!TrashItem(manager, Selector("trashItemAtURL:resultingItemURL:error:"), url, IntPtr.Zero, out var error))
            {
                throw new IOException(Describe(error) ?? $"Could not move '{path}' to the Trash.");
            }
        }
        finally
        {
            _ = Send(pool, "drain");
        }
    }

    private static string? Describe(IntPtr error)
    {
        if (error == IntPtr.Zero)
        {
            return null;
        }

        var description = Send(error, "localizedDescription");
        return description == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(description, "UTF8String"));
    }

    private static IntPtr Class(string name) => GetClass(Utf8(name));

    private static IntPtr Selector(string name) => RegisterName(Utf8(name));

    /// <summary>A NUL-terminated UTF-8 string for the C side.</summary>
    private static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text + "\0");

    private static IntPtr Send(IntPtr receiver, string selector) => SendNone(receiver, Selector(selector));

    private static IntPtr SendString(IntPtr receiver, string selector, string argument) => SendUtf8(receiver, Selector(selector), Utf8(argument));

    private static IntPtr SendPointer(IntPtr receiver, string selector, IntPtr argument) => SendOne(receiver, Selector(selector), argument);

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(byte[] name);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr RegisterName(byte[] name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendNone(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendOne(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendUtf8(IntPtr receiver, IntPtr selector, byte[] argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool TrashItem(IntPtr receiver, IntPtr selector, IntPtr url, IntPtr resulting, out IntPtr error);
}
