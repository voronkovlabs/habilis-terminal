using System.Globalization;
using Avalonia.Input;
using HabilisTerminal.Shell.ConPty;
using HabilisTerminal.Terminal;

namespace HabilisTerminal.App.Hosting;

/// <summary>
/// Keys to what a console reads (issue #253): win32-input-mode sequences while ConPTY asks for them, which carry every
/// key exactly (F24, Ctrl+Space), plain VT sequences otherwise.
/// </summary>
internal static class TerminalKeys
{
    /// <summary>The sequence for a key, or null when the key types text and comes as text input instead.</summary>
    public static string? Encode(Key key, KeyModifiers modifiers, string? symbol, TerminalEmulator emulator)
    {
        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var typed = symbol is { Length: > 0 } && !char.IsControl(symbol[0]) ? symbol : null;

        // Text, also AltGr's (Ctrl+Alt with a character), comes through TextInput.
        if (typed is not null && (!ctrl && !alt || ctrl && alt))
        {
            return null;
        }

        if (VirtualKey(key) is not { } vk || vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C)
        {
            return null;
        }

        if (emulator.Win32InputMode)
        {
            var state = (shift ? Win32InputSequence.Shift : 0) | (ctrl ? Win32InputSequence.LeftCtrl : 0) |
                (alt ? Win32InputSequence.LeftAlt : 0) | (IsEnhanced(key) ? Win32InputSequence.EnhancedKey : 0);
            return Win32InputSequence.KeyPress(vk, CharOf(key, vk, ctrl, typed), state);
        }

        return Vt(key, ctrl, alt, shift, typed, emulator.ApplicationCursorKeys);
    }

    /// <summary>The character a console key record carries: control characters for Ctrl+letter.</summary>
    private static char CharOf(Key key, int vk, bool ctrl, string? typed)
    {
        if (ctrl && vk is >= 0x41 and <= 0x5A)
        {
            return (char)(vk - 0x40);
        }

        return key switch
        {
            Key.Enter => '\r',
            Key.Back => '\b',
            Key.Tab => '\t',
            Key.Escape => '\u001b',
            Key.Space => ' ',
            _ => typed is { Length: 1 } ? typed[0] : '\0',
        };
    }

    private static string? Vt(Key key, bool ctrl, bool alt, bool shift, string? typed, bool applicationCursor)
    {
        var mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        string Csi(string final) => mod == 1 ? "\u001b[" + final : string.Create(CultureInfo.InvariantCulture, $"\u001b[1;{mod}{final}");
        string Tilde(int code) => mod == 1 ? string.Create(CultureInfo.InvariantCulture, $"\u001b[{code}~") : string.Create(CultureInfo.InvariantCulture, $"\u001b[{code};{mod}~");
        string Cursor(char final) => mod == 1 ? (applicationCursor ? "\u001bO" : "\u001b[") + final : Csi(final.ToString());

        var text = key switch
        {
            Key.Up => Cursor('A'),
            Key.Down => Cursor('B'),
            Key.Right => Cursor('C'),
            Key.Left => Cursor('D'),
            Key.Home => Cursor('H'),
            Key.End => Cursor('F'),
            Key.Insert => Tilde(2),
            Key.Delete => Tilde(3),
            Key.PageUp => Tilde(5),
            Key.PageDown => Tilde(6),
            Key.F1 => mod == 1 ? "\u001bOP" : Csi("P"),
            Key.F2 => mod == 1 ? "\u001bOQ" : Csi("Q"),
            Key.F3 => mod == 1 ? "\u001bOR" : Csi("R"),
            Key.F4 => mod == 1 ? "\u001bOS" : Csi("S"),
            Key.F5 => Tilde(15),
            Key.F6 => Tilde(17),
            Key.F7 => Tilde(18),
            Key.F8 => Tilde(19),
            Key.F9 => Tilde(20),
            Key.F10 => Tilde(21),
            Key.F11 => Tilde(23),
            Key.F12 => Tilde(24),
            Key.Enter => "\r",
            Key.Back => ctrl ? "\b" : "\u007f",
            Key.Tab => shift ? "\u001b[Z" : "\t",
            Key.Escape => "\u001b",
            Key.Space when ctrl => "\0",
            >= Key.A and <= Key.Z when ctrl => ((char)(key - Key.A + 1)).ToString(),
            _ => typed,
        };

        return text is not null && alt && !text.StartsWith('\u001b') ? "\u001b" + text : text;
    }

    private static bool IsEnhanced(Key key) => key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or
        Key.PageUp or Key.PageDown or Key.Insert or Key.Delete or Key.Divide or Key.RightCtrl or Key.RightAlt;

    /// <summary>The Windows virtual key of an Avalonia key (the enums share WPF's order, not Windows' codes).</summary>
    public static int? VirtualKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 0x41 + (key - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30 + (key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => 0x70 + (key - Key.F1),
        Key.Back => 0x08,
        Key.Tab => 0x09,
        Key.Clear => 0x0C,
        Key.Enter => 0x0D,
        Key.LeftShift or Key.RightShift => 0x10,
        Key.LeftCtrl or Key.RightCtrl => 0x11,
        Key.LeftAlt or Key.RightAlt => 0x12,
        Key.Pause => 0x13,
        Key.CapsLock => 0x14,
        Key.Escape => 0x1B,
        Key.Space => 0x20,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        Key.End => 0x23,
        Key.Home => 0x24,
        Key.Left => 0x25,
        Key.Up => 0x26,
        Key.Right => 0x27,
        Key.Down => 0x28,
        Key.PrintScreen => 0x2C,
        Key.Insert => 0x2D,
        Key.Delete => 0x2E,
        Key.LWin => 0x5B,
        Key.RWin => 0x5C,
        Key.Apps => 0x5D,
        Key.Multiply => 0x6A,
        Key.Add => 0x6B,
        Key.Separator => 0x6C,
        Key.Subtract => 0x6D,
        Key.Decimal => 0x6E,
        Key.Divide => 0x6F,
        Key.NumLock => 0x90,
        Key.Scroll => 0x91,
        Key.OemSemicolon => 0xBA,
        Key.OemPlus => 0xBB,
        Key.OemComma => 0xBC,
        Key.OemMinus => 0xBD,
        Key.OemPeriod => 0xBE,
        Key.OemQuestion => 0xBF,
        Key.OemTilde => 0xC0,
        Key.OemOpenBrackets => 0xDB,
        Key.OemPipe => 0xDC,
        Key.OemCloseBrackets => 0xDD,
        Key.OemQuotes => 0xDE,
        Key.OemBackslash => 0xE2,
        _ => null,
    };
}
