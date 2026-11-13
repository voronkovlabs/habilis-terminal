using System.Reflection;
using HabilisTerminal.App.Hosting;
using HabilisTerminal.App.Theming;

namespace HabilisTerminal.App.Tests;

/// <summary>What the Mac build draws and types differently (issues #261, #268).</summary>
public sealed class MacTests
{
    [Fact]
    public void Every_codicon_the_app_names_has_an_outline()
    {
        var named = typeof(Codicon).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (Name: f.Name, Glyph: (string)f.GetRawConstantValue()!));
        foreach (var (name, glyph) in named)
        {
            Assert.True(CodiconOutlines.All.ContainsKey(glyph[0]), $"{name} has no outline");
        }

        foreach (var chevron in new[] { '\uEAB5', '\uEAB6' })
        {
            Assert.True(CodiconOutlines.All.ContainsKey(chevron));
        }
    }

    [Theory]
    [InlineData("plain.txt", "plain.txt")]
    [InlineData("a b.txt", "'a b.txt'")]
    [InlineData("it's", @"'it'\''s'")]
    [InlineData("$HOME", "'$HOME'")]
    public void A_posix_shell_gets_single_quotes(string text, string expected) =>
        Assert.Equal(expected, ShellQuote.Quote(text, posix: true));
}
