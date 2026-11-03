using System.Globalization;

namespace HabilisTerminal.Shell.ConPty;

/// <summary>
/// Encodes key presses in win32-input-mode (<c>ESC [ Vk ; Sc ; Uc ; Kd ; Cs ; Rc _</c>), which ConPTY turns
/// back into full console key records — the only way to deliver keys such as F24 or Ctrl+Space exactly.
/// </summary>
public static class Win32InputSequence
{
    // dwControlKeyState flags.
    public const int RightAlt = 0x0001;
    public const int LeftAlt = 0x0002;
    public const int RightCtrl = 0x0004;
    public const int LeftCtrl = 0x0008;
    public const int Shift = 0x0010;
    public const int EnhancedKey = 0x0100;

    public const int VkSpace = 0x20;
    public const int VkUp = 0x26;
    public const int VkDown = 0x28;
    public const int VkF23 = 0x86;
    public const int VkF24 = 0x87;

    /// <summary>Key down followed by key up.</summary>
    public static string KeyPress(int virtualKey, char unicodeChar = '\0', int controlKeyState = 0) =>
        Encode(virtualKey, unicodeChar, keyDown: true, controlKeyState) +
        Encode(virtualKey, unicodeChar, keyDown: false, controlKeyState);

    public static string Encode(int virtualKey, char unicodeChar, bool keyDown, int controlKeyState) =>
        string.Create(CultureInfo.InvariantCulture, $"\u001b[{virtualKey};0;{(int)unicodeChar};{(keyDown ? 1 : 0)};{controlKeyState};1_");
}
