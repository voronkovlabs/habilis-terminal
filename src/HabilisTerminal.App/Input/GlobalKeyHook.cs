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
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MOUSEWHEEL = 0x020A;

    private readonly Func<Key, KeyModifiers, bool> _handler;
    private readonly Func<MSG, KeyModifiers, bool>? _mouseHandler;

    /// <param name="handler">Returns true when the key is consumed and must not reach its target.</param>
    /// <param name="mouseHandler">Right button down and wheel messages; returns true when consumed.</param>
    public GlobalKeyHook(Func<Key, KeyModifiers, bool> handler, Func<MSG, KeyModifiers, bool>? mouseHandler = null)
    {
        _handler = handler;
        _mouseHandler = mouseHandler;
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
    }

    public void Dispose() => ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;

    private void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (handled)
        {
            return;
        }

        if (msg.message is WM_RBUTTONDOWN or WM_MOUSEWHEEL)
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
}
