using System.Runtime.InteropServices;
using System.Windows.Input;

namespace HabilisTerminal.App.Input;

/// <summary>The character a key produces in the current keyboard layout (for Alt+letter quick search).</summary>
public static class KeyText
{
    public static char? CharOf(Key key, bool shift)
    {
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        var state = new byte[256];
        if (shift)
        {
            state[0x10] = 0x80;
        }

        var buffer = new char[4];
        var layout = GetKeyboardLayout(0);
        var scanCode = MapVirtualKeyEx(virtualKey, 0, layout);

        // Flag 0x4: do not change the keyboard state (no dead-key side effects).
        var count = ToUnicodeEx(virtualKey, scanCode, state, buffer, buffer.Length, 0x4, layout);
        return count == 1 && !char.IsControl(buffer[0]) ? buffer[0] : null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState, [Out] char[] pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);
}
