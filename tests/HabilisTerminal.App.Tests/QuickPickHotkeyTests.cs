using Avalonia.Input;
using HabilisTerminal.App.Widgets;

namespace HabilisTerminal.App.Tests;

/// <summary>Menu hotkeys go by the key, whatever the layout types (issue #394).</summary>
public class QuickPickHotkeyTests
{
    [Theory]
    [InlineData(Key.C, 'C')]
    [InlineData(Key.Z, 'Z')]
    [InlineData(Key.D0, '0')]
    [InlineData(Key.D9, '9')]
    [InlineData(Key.NumPad3, '3')]
    public void Letter_and_digit_keys_give_their_Latin_character(Key key, char expected) =>
        Assert.Equal(expected, QuickPick.HotkeyOf(key));

    [Theory]
    [InlineData(Key.OemComma)]
    [InlineData(Key.Space)]
    [InlineData(Key.F1)]
    public void Other_keys_are_no_hotkey(Key key) => Assert.Null(QuickPick.HotkeyOf(key));
}
