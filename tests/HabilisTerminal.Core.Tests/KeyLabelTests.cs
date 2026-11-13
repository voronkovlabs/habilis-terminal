using HabilisTerminal.Core.Commands;

namespace HabilisTerminal.Core.Tests;

/// <summary>Keys as a Mac shows them (issue #261); Windows keeps the text.</summary>
public class KeyLabelTests
{
    [Theory]
    [InlineData("Ctrl+Shift+P", "⌃⇧P")]
    [InlineData("Shift+Ctrl+P", "⌃⇧P")]
    [InlineData("Alt+F1", "⌥F1")]
    [InlineData("Ctrl+Alt+=", "⌃⌥=")]
    [InlineData("F3", "F3")]
    [InlineData("Shift+Del", "⇧⌦")]
    [InlineData("Ctrl+Enter", "⌃↩")]
    [InlineData("Esc", "⎋")]
    [InlineData("Gray +", "Gray +")]
    [InlineData("Ctrl+1…5, Ctrl+0", "⌃1…5, ⌃0")]
    [InlineData("Ctrl+Up/Down", "⌃Up/Down")]
    public void A_mac_shows_symbols(string keys, string expected) => Assert.Equal(expected, KeyLabel.Show(keys, mac: true));

    [Fact]
    public void Windows_keeps_the_text() => Assert.Equal("Ctrl+Shift+P", KeyLabel.Show("Ctrl+Shift+P", mac: false));
}
