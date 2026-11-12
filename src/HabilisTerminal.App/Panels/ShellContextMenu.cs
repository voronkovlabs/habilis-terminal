#if WINDOWS
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Input;

namespace HabilisTerminal.App.Panels;

/// <summary>
/// The Windows shell context menu, the one Explorer shows (SPEC §6, issue #41): for items of one folder, or for the
/// folder's background when no item is given. Runs on the UI (STA) thread; owner-drawn submenus such as "Send to" and
/// "Open with" get their messages through <c>IContextMenu2/3</c> while the menu is open.
/// </summary>
internal static class Win32ShellContextMenu
{
    /// <summary>Shows the menu at <paramref name="screen"/> (physical pixels) and runs the chosen command.</summary>
    /// <param name="intercept">Gets the chosen command's verb first (e.g. "rename"); returns true when it handled it.</param>
    /// <returns>True when a command was chosen.</returns>
    public static bool Show(Window owner, string folder, IReadOnlyList<string> names, PixelPoint screen, Func<string, bool>? intercept = null)
    {
        var hwnd = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var absolute = IntPtr.Zero;
        var children = new List<IntPtr>();
        IShellFolder? desktop = null;
        IShellFolder? parent = null;
        IContextMenu? menu = null;
        var hMenu = IntPtr.Zero;
        Win32Properties.CustomWndProcHookCallback? hook = null;
        try
        {
            Check(SHGetDesktopFolder(out desktop));
            uint eaten = 0;
            uint attributes = 0;
            Check(desktop.ParseDisplayName(hwnd, IntPtr.Zero, folder, ref eaten, out absolute, ref attributes));
            var shellFolderId = typeof(IShellFolder).GUID;
            Check(desktop.BindToObject(absolute, IntPtr.Zero, ref shellFolderId, out var parentPointer));
            parent = (IShellFolder)Marshal.GetTypedObjectForIUnknown(parentPointer, typeof(IShellFolder));
            Marshal.Release(parentPointer);

            var menuId = typeof(IContextMenu).GUID;
            IntPtr menuPointer;
            if (names.Count == 0)
            {
                Check(parent.CreateViewObject(hwnd, ref menuId, out menuPointer));
            }
            else
            {
                foreach (var name in names)
                {
                    uint childEaten = 0;
                    uint childAttributes = 0;
                    Check(parent.ParseDisplayName(hwnd, IntPtr.Zero, name, ref childEaten, out var child, ref childAttributes));
                    children.Add(child);
                }

                Check(parent.GetUIObjectOf(hwnd, (uint)children.Count, [.. children], ref menuId, IntPtr.Zero, out menuPointer));
            }

            menu = (IContextMenu)Marshal.GetTypedObjectForIUnknown(menuPointer, typeof(IContextMenu));
            Marshal.Release(menuPointer);

            hMenu = CreatePopupMenu();
            var flags = CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME;
            if (Keyboard.Modifiers.HasFlag(KeyModifiers.Shift))
            {
                flags |= CMF_EXTENDEDVERBS;
            }

            Check(menu.QueryContextMenu(hMenu, 0, FirstCommand, LastCommand, flags));

            var menu2 = menu as IContextMenu2;
            var menu3 = menu as IContextMenu3;
            if (menu2 is not null)
            {
                hook = (IntPtr _, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg is not (WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR))
                    {
                        return IntPtr.Zero;
                    }

                    if (menu3 is not null && menu3.HandleMenuMsg2(msg, wParam, lParam, out var result) == 0)
                    {
                        handled = true;
                        return result;
                    }

                    if (menu2.HandleMenuMsg(msg, wParam, lParam) == 0)
                    {
                        handled = true;
                    }

                    return IntPtr.Zero;
                };
                Win32Properties.AddWndProcHookCallback(owner, hook);
            }

            var command = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, (int)screen.X, (int)screen.Y, hwnd, IntPtr.Zero);
            if (command < FirstCommand)
            {
                return false;
            }

            var offset = (uint)command - FirstCommand;
            if (intercept is not null && VerbOf(menu, offset) is { } verb && intercept(verb))
            {
                return true;
            }

            var directory = Marshal.StringToHGlobalUni(folder);
            try
            {
                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                    fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                    hwnd = hwnd,
                    lpVerb = (IntPtr)offset,
                    lpVerbW = (IntPtr)offset,
                    lpDirectoryW = directory,
                    nShow = SW_SHOWNORMAL,
                    ptInvoke = new POINT { X = (int)screen.X, Y = (int)screen.Y },
                };
                if (Keyboard.Modifiers.HasFlag(KeyModifiers.Control))
                {
                    info.fMask |= CMIC_MASK_CONTROL_DOWN;
                }

                if (Keyboard.Modifiers.HasFlag(KeyModifiers.Shift))
                {
                    info.fMask |= CMIC_MASK_SHIFT_DOWN;
                }

                // A failed or cancelled command (e.g. a dialog closed with Cancel) is not an error for the panel.
                _ = menu.InvokeCommand(ref info);
            }
            finally
            {
                Marshal.FreeHGlobal(directory);
            }

            return true;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            if (hook is not null)
            {
                Win32Properties.RemoveWndProcHookCallback(owner, hook);
            }

            if (hMenu != IntPtr.Zero)
            {
                DestroyMenu(hMenu);
            }

            foreach (var child in children)
            {
                Marshal.FreeCoTaskMem(child);
            }

            if (absolute != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(absolute);
            }

            Release(menu);
            Release(parent);
            Release(desktop);
        }
    }

    private static string? VerbOf(IContextMenu menu, uint offset)
    {
        const int capacity = 256;
        var buffer = Marshal.AllocHGlobal(capacity * 2);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            return menu.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buffer, capacity) == 0
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void Check(int hresult)
    {
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            Marshal.ReleaseComObject(com);
        }
    }

    private const uint FirstCommand = 1;
    private const uint LastCommand = 0x7FFF;

    private const uint CMF_NORMAL = 0x0;
    private const uint CMF_EXPLORE = 0x4;
    private const uint CMF_CANRENAME = 0x10;
    private const uint CMF_EXTENDEDVERBS = 0x100;

    private const uint CMIC_MASK_UNICODE = 0x4000;
    private const uint CMIC_MASK_SHIFT_DOWN = 0x10000000;
    private const uint CMIC_MASK_PTINVOKE = 0x20000000;
    private const uint CMIC_MASK_CONTROL_DOWN = 0x40000000;

    private const uint GCS_VERBW = 0x4;
    private const int SW_SHOWNORMAL = 1;

    private const uint TPM_RIGHTBUTTON = 0x2;
    private const uint TPM_RETURNCMD = 0x100;

    private const int WM_INITMENUPOPUP = 0x0117;
    private const int WM_DRAWITEM = 0x002B;
    private const int WM_MEASUREITEM = 0x002C;
    private const int WM_MENUCHAR = 0x0120;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public uint cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public POINT ptInvoke;
    }

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

        [PreserveSig]
        int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);

        [PreserveSig]
        int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

        [PreserveSig]
        int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);

        [PreserveSig]
        int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);

        [PreserveSig]
        int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);

        [PreserveSig]
        int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);

        [PreserveSig]
        int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder([MarshalAs(UnmanagedType.Interface)] out IShellFolder ppshf);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);
}
#endif
