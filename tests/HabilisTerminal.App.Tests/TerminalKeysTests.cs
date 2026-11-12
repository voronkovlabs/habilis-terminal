using Avalonia.Input;
using HabilisTerminal.App.Hosting;
using HabilisTerminal.Terminal;

namespace HabilisTerminal.App.Tests;

/// <summary>What the console view sends for keys (issue #253): win32-input-mode for ConPTY, VT otherwise.</summary>
public sealed class TerminalKeysTests
{
    private const string Esc = "\u001b";

    private static TerminalEmulator Win32Mode()
    {
        var emulator = new TerminalEmulator(80, 24);
        emulator.Feed(Esc + "[?9001h");
        return emulator;
    }

    [Fact]
    public void Typed_text_comes_as_text_input()
    {
        var emulator = Win32Mode();
        Assert.Null(TerminalKeys.Encode(Key.A, KeyModifiers.None, "a", emulator));
        Assert.Null(TerminalKeys.Encode(Key.A, KeyModifiers.Shift, "A", emulator));

        // AltGr is Ctrl+Alt with a character: still text.
        Assert.Null(TerminalKeys.Encode(Key.Q, KeyModifiers.Control | KeyModifiers.Alt, "@", emulator));

        // Modifier keys alone send nothing.
        Assert.Null(TerminalKeys.Encode(Key.LeftShift, KeyModifiers.Shift, null, emulator));
    }

    [Fact]
    public void Win32_input_mode_carries_the_key_record()
    {
        var emulator = Win32Mode();
        Assert.Equal(Esc + "[67;0;3;1;8;1_" + Esc + "[67;0;3;0;8;1_", TerminalKeys.Encode(Key.C, KeyModifiers.Control, "c", emulator));
        Assert.Equal(Esc + "[38;0;0;1;256;1_" + Esc + "[38;0;0;0;256;1_", TerminalKeys.Encode(Key.Up, KeyModifiers.None, null, emulator));
        Assert.Equal(Esc + "[13;0;13;1;0;1_" + Esc + "[13;0;13;0;0;1_", TerminalKeys.Encode(Key.Enter, KeyModifiers.None, "\r", emulator));
        Assert.Equal(Esc + "[116;0;0;1;16;1_" + Esc + "[116;0;0;0;16;1_", TerminalKeys.Encode(Key.F5, KeyModifiers.Shift, null, emulator));
    }

    [Fact]
    public void Vt_sequences_without_win32_input_mode()
    {
        var emulator = new TerminalEmulator(80, 24);
        Assert.Equal(Esc + "[A", TerminalKeys.Encode(Key.Up, KeyModifiers.None, null, emulator));
        Assert.Equal(Esc + "[1;5A", TerminalKeys.Encode(Key.Up, KeyModifiers.Control, null, emulator));
        Assert.Equal(Esc + "[15~", TerminalKeys.Encode(Key.F5, KeyModifiers.None, null, emulator));
        Assert.Equal(Esc + "OP", TerminalKeys.Encode(Key.F1, KeyModifiers.None, null, emulator));
        Assert.Equal("\u0003", TerminalKeys.Encode(Key.C, KeyModifiers.Control, "c", emulator));
        Assert.Equal(Esc + "x", TerminalKeys.Encode(Key.X, KeyModifiers.Alt, "x", emulator));
        Assert.Equal("\u007f", TerminalKeys.Encode(Key.Back, KeyModifiers.None, "\b", emulator));
        Assert.Equal(Esc + "[Z", TerminalKeys.Encode(Key.Tab, KeyModifiers.Shift, "\t", emulator));

        emulator.Feed(Esc + "[?1h");
        Assert.Equal(Esc + "OA", TerminalKeys.Encode(Key.Up, KeyModifiers.None, null, emulator));
    }

    [Theory]
    [InlineData(Key.A, 0x41)]
    [InlineData(Key.D7, 0x37)]
    [InlineData(Key.F12, 0x7B)]
    [InlineData(Key.F24, 0x87)]
    [InlineData(Key.NumPad5, 0x65)]
    [InlineData(Key.OemPlus, 0xBB)]
    [InlineData(Key.Delete, 0x2E)]
    public void Virtual_keys_are_windows_codes(Key key, int code) => Assert.Equal(code, TerminalKeys.VirtualKey(key));
}
