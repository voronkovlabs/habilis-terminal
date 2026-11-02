using System.Net;
using Markdig;

namespace HabilisTerminal.Core.Viewing;

/// <summary>Colours of the rendered Markdown page, as CSS colours (taken from the colour theme by the caller).</summary>
public sealed record MarkdownColors(
    string Background,
    string Foreground,
    string Muted,
    string Link,
    string Border,
    string CodeBackground,
    bool IsLight);

/// <summary>
/// Alt+F3 on a Markdown file (issue #57): the file as a complete HTML page in a GitHub-like style — tables, task
/// lists, footnotes, autolinks and the other Markdig advanced extensions. Raw HTML in the file is escaped, and
/// relative links and images resolve against <paramref name="baseHref"/> (the file's folder, served by the viewer).
/// </summary>
public static class MarkdownHtml
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static string Render(string markdown, MarkdownColors colors, string baseHref, string title)
    {
        var body = Markdown.ToHtml(markdown, Pipeline);
        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src * data:; style-src 'unsafe-inline'">
            <base href="{{WebUtility.HtmlEncode(baseHref)}}">
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>
            :root { color-scheme: {{(colors.IsLight ? "light" : "dark")}}; }
            body { margin: 0; padding: 24px 32px 48px; background: {{colors.Background}}; color: {{colors.Foreground}};
              font: 15px/1.6 "Segoe UI", system-ui, sans-serif; }
            main { max-width: 900px; margin: 0 auto; }
            h1, h2 { border-bottom: 1px solid {{colors.Border}}; padding-bottom: .3em; }
            h1, h2, h3, h4, h5, h6 { margin: 1.4em 0 .6em; line-height: 1.25; font-weight: 600; }
            p, ul, ol, table, pre, blockquote { margin: 0 0 1em; }
            a { color: {{colors.Link}}; text-decoration: none; }
            a:hover { text-decoration: underline; }
            code, pre { font-family: "Cascadia Mono", Consolas, monospace; font-size: 13px; }
            code { background: {{colors.CodeBackground}}; padding: .15em .35em; border-radius: 4px; }
            pre { background: {{colors.CodeBackground}}; padding: 12px 16px; border-radius: 6px; overflow: auto; }
            pre code { background: none; padding: 0; }
            blockquote { color: {{colors.Muted}}; border-left: 4px solid {{colors.Border}}; padding: 0 1em; }
            table { border-collapse: collapse; }
            th, td { border: 1px solid {{colors.Border}}; padding: 6px 12px; }
            th { font-weight: 600; }
            img { max-width: 100%; }
            hr { border: 0; border-top: 1px solid {{colors.Border}}; }
            ul.contains-task-list { list-style: none; padding-left: 1.2em; }
            </style>
            </head>
            <body><main>
            {{body}}
            </main></body>
            </html>
            """;
    }
}
