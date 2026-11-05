using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace HabilisTerminal.App.Input;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
}

/// <summary>
/// Sees every keyboard message of the UI thread before it is dispatched, including messages
/// addressed to the native terminal window, which WPF input events never see (SPEC §4).
/// </summary>
public sealed class GlobalKeyHook : IDisposable
{
    private const int WM_KEYFIRST = 0x0100;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_CHAR = 0x0102;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYLAST = 0x0109;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MOUSEWHEEL = 0x020A;

    private readonly Func<Key, KeyModifiers, bool> _handler;
    private readonly Func<MSG, KeyModifiers, bool>? _mouseHandler;
    private readonly Func<IntPtr, bool>? _isConsole;
    private readonly Func<char, bool>? _capturesText;

    /// <param name="handler">Returns true when the key is consumed and must not reach its target.</param>
    /// <param name="mouseHandler">Left and right button down and wheel messages; returns true when consumed.</param>
    /// <param name="isConsole">
    /// Whether a window belongs to the console. Its keyboard messages bypass WPF (issue #90): with the Win32 focus in
    /// the console WPF has no focused element, and its keyboard navigation would move the focus to a panel on an
    /// arrow key. They go to the handler first and are then dispatched to the console as the message loop would.
    /// </param>
    /// <param name="capturesText">
    /// Whether text is captured instead of going to the console (quick search). Called with '\0' to ask, and with each
    /// typed character, whatever produced it: a key in any layout, a dead key, injected text. Nothing reaches the
    /// console meanwhile.
    /// </param>
    public GlobalKeyHook(
        Func<Key, KeyModifiers, bool> handler,
        Func<MSG, KeyModifiers, bool>? mouseHandler = null,
        Func<IntPtr, bool>? isConsole = null,
        Func<char, bool>? capturesText = null)
    {
        _capturesText = capturesText;
        _handler = handler;
        _mouseHandler = mouseHandler;
        _isConsole = isConsole;
        ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
    }

    public void Dispose()
    {
        ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
        ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;
    }

    private void OnThreadFilterMessage(ref MSG msg, ref bool handled)
    {
        if (handled || msg.message is < WM_KEYFIRST or > WM_KEYLAST || _isConsole?.Invoke(msg.hwnd) != true)
        {
            return;
        }

        handled = true;
        if (msg.message is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            if (_handler(KeyInterop.KeyFromVirtualKey((int)msg.wParam), CurrentModifiers()))
            {
                return;
            }

            TranslateMessage(ref msg);
        }

        if (msg.message == WM_CHAR)
        {
            if (_capturesText?.Invoke((char)msg.wParam) == true)
            {
                return;
            }
        }
        else if (_capturesText?.Invoke('\0') == true)
        {
            return;
        }

        using var measure = Core.Diagnostics.Perf.Measure("console.key-message");
        DispatchMessage(ref msg);
    }

    private void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (handled)
        {
            return;
        }

        if (msg.message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MOUSEWHEEL)
        {
            handled = _mouseHandler?.Invoke(msg, CurrentModifiers()) == true;
            return;
        }

        if (msg.message != WM_KEYDOWN && msg.message != WM_SYSKEYDOWN)
        {
            return;
        }

        var key = KeyInterop.KeyFromVirtualKey((int)msg.wParam);
        if (_handler(key, CurrentModifiers()))
        {
            handled = true;
        }
    }

    private static KeyModifiers CurrentModifiers()
    {
        var modifiers = KeyModifiers.None;
        if (IsDown(VK_MENU))
        {
            modifiers |= KeyModifiers.Alt;
        }

        if (IsDown(VK_CONTROL))
        {
            modifiers |= KeyModifiers.Control;
        }

        if (IsDown(VK_SHIFT))
        {
            modifiers |= KeyModifiers.Shift;
        }

        return modifiers;
    }

    private static bool IsDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    // The Unicode entry point: the ANSI one cuts typed characters down to one byte.
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref MSG msg);
}
