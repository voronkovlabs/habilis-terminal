using Xunit;

namespace HabilisTerminal.Themes.Tests;

/// <summary>Syntax colours as VS Code shows them with the same theme (issue #207).</summary>
public sealed class TextMateSyntaxTests
{
    private static readonly string[] CSharp =
    [
        "using System;",
        "/* a comment",
        "   still a comment */",
        "public sealed class Greeter",
        "{",
        "    public void Say(string name) { if (name == null) return; Console.WriteLine($\"Hi {name}\" + 42); }",
        "}",
    ];

    [Fact]
    public void Dark_modern_colours_csharp_as_vs_code_does()
    {
        // Theme state is shared; the tests in this class run one after another.
        lock (typeof(TextMateSyntax))
        {
            TextMateSyntax.SetTheme(ThemeLoader.LoadBuiltIn(ThemeLoader.DarkModern));
            var grammar = TextMateSyntax.GrammarFor(".cs");
            Assert.NotNull(grammar);
            var tokens = TextMateSyntax.Tokenize(grammar, CSharp);

            Assert.Equal("#569CD6", ColorOf(tokens, 4, "public"));    // storage modifier
            Assert.Equal("#4EC9B0", ColorOf(tokens, 4, "Greeter"));   // class name
            Assert.Equal("#DCDCAA", ColorOf(tokens, 6, "Say"));       // method name
            Assert.Equal("#9CDCFE", ColorOf(tokens, 6, "name"));      // parameter
            Assert.Equal("#C586C0", ColorOf(tokens, 6, "if"));        // control keyword
            Assert.Equal("#C586C0", ColorOf(tokens, 6, "return"));
            Assert.Equal("#CE9178", ColorOf(tokens, 6, "Hi "));       // string
            Assert.Equal("#B5CEA8", ColorOf(tokens, 6, "42"));        // number
            Assert.Equal("#6A9955", ColorOf(tokens, 3, "still"));     // a comment carried over from the line before
        }
    }

    [Fact]
    public void Light_modern_and_other_languages_follow_the_theme()
    {
        lock (typeof(TextMateSyntax))
        {
            TextMateSyntax.SetTheme(ThemeLoader.LoadBuiltIn(ThemeLoader.LightModern));
            var tokens = TextMateSyntax.Tokenize(TextMateSyntax.GrammarFor(".cs")!, CSharp);
            Assert.Equal("#267F99", ColorOf(tokens, 4, "Greeter"));
            Assert.Equal("#795E26", ColorOf(tokens, 6, "Say"));

            string[] script = ["# note", "$x = Get-Item 'a'"];
            var powershell = TextMateSyntax.Tokenize(TextMateSyntax.GrammarFor(".ps1")!, script);
            Assert.Equal("#008000", ColorOf(script, powershell, 1, "note"));
            Assert.Equal("#A31515", ColorOf(script, powershell, 2, "'a'"));

            // No grammar for the extension: the language id from AvalonEdit's definition finds one.
            Assert.Null(TextMateSyntax.GrammarFor(".no-such-extension"));
            Assert.NotNull(TextMateSyntax.GrammarFor(".csproj", "xml"));
        }
    }

    private static string? ColorOf(List<(int Line, int Start, int End, ThemeColor? Color, TextMateSharp.Themes.FontStyle Style)> tokens, int line, string word) =>
        ColorOf(CSharp, tokens, line, word);

    /// <summary>The colour of the token at the first <paramref name="word"/> in the line, as #RRGGBB.</summary>
    private static string? ColorOf(string[] lines, List<(int Line, int Start, int End, ThemeColor? Color, TextMateSharp.Themes.FontStyle Style)> tokens, int line, string word)
    {
        var at = lines[line - 1].IndexOf(word, StringComparison.Ordinal);
        Assert.True(at >= 0, word);
        var token = tokens.Single(t => t.Line == line && t.Start <= at && at < t.End);
        return token.Color is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : null;
    }
}
