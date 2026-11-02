using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.Core.Tests;

public class MarkdownHtmlTests
{
    private static readonly MarkdownColors Dark = new("#1e1e1e", "#d4d4d4", "#9d9d9d", "#3794ff", "#444444", "#2d2d2d", IsLight: false);

    [Fact]
    public void Renders_headings_tables_task_lists_and_code()
    {
        var html = MarkdownHtml.Render("# Title\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n- [x] done\n\n```cs\nvar x = 1;\n```\n", Dark, "https://md.habi/", "readme.md");

        Assert.Contains("<h1 id=\"title\">Title</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<table>", html, StringComparison.Ordinal);
        Assert.Contains("<td>2</td>", html, StringComparison.Ordinal);
        Assert.Contains("type=\"checkbox\"", html, StringComparison.Ordinal);
        Assert.Contains("<code class=\"language-cs\">var x = 1;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_html_is_escaped_and_scripts_are_blocked()
    {
        var html = MarkdownHtml.Render("<script>alert(1)</script>\n\nText <b>bold</b>", Dark, "https://md.habi/", "x.md");

        Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal); // no script-src: nothing runs
    }

    [Fact]
    public void Theme_colours_title_and_base_go_into_the_page()
    {
        var html = MarkdownHtml.Render("[next](docs/next.md) ![logo](img/logo.png)", Dark, "https://md.habi/", "a&b.md");

        Assert.Contains("<base href=\"https://md.habi/\">", html, StringComparison.Ordinal);
        Assert.Contains("<title>a&amp;b.md</title>", html, StringComparison.Ordinal);
        Assert.Contains("background: #1e1e1e", html, StringComparison.Ordinal);
        Assert.Contains("color-scheme: dark", html, StringComparison.Ordinal);
        Assert.Contains("href=\"docs/next.md\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"img/logo.png\"", html, StringComparison.Ordinal);
    }
}
