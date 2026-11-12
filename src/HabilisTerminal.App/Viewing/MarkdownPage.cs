using System.Globalization;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using HabilisTerminal.App.Theming;
using HabilisTerminal.Themes;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Inline = Avalonia.Controls.Documents.Inline;

namespace HabilisTerminal.App.Viewing;

/// <summary>
/// Alt+F3 on a Markdown file (issue #57): the file rendered in the program, coloured by the colour theme. On Avalonia
/// (issue #253) the Markdig syntax tree is drawn with controls instead of HTML in WebView2: headings, paragraphs with
/// emphasis and code, lists and task lists, quotes, code blocks, tables, rules and local images. Links to other
/// Markdown files open here, "#anchors" scroll to their heading, web links open in the browser. Text can be selected
/// and copied. F3 the plain viewer, F4 / F6 the editor, Esc / F10 / Alt+F3 close.
/// </summary>
public sealed class MarkdownPage : DockPanel, IPage
{
    private const double LineStep = 40;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdown", ".mkd" };
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private const double MaxTextWidth = 980;
    private readonly StackPanel _body = new() { Margin = new Thickness(32, 20, 32, 40), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _title = new();
    private readonly Dictionary<string, Control> _anchors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Colors _colors;

    public MarkdownPage(string path, ColorTheme theme)
    {
        _colors = new Colors(theme);
        Focusable = true;
        Background = _colors.Background;
        LastChildFill = true;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        var hint = new TextBlock { Text = "F3 view as text · F4 edit · Esc close" };
        foreach (var text in new[] { _title, hint })
        {
            text.FontFamily = new FontFamily("Segoe UI");
            text.FontSize = 12;
            text.Foreground = text == _title ? PanelPalette.Foreground : PanelPalette.DescriptionForeground;
            text.Margin = new Thickness(0, 0, 16, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
        }

        _title.FontWeight = FontWeight.SemiBold;
        var header = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
        DockPanel.SetDock(hint, Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(_title);
        var headerBorder = new Border { Child = header, BorderBrush = PanelPalette.Border, BorderThickness = new Thickness(0, 0, 0, 1), Background = PanelPalette.HeaderBackground };
        DockPanel.SetDock(headerBorder, Dock.Top);
        Children.Add(headerBorder);

        _scroll.Content = _body;

        // As wide as the page allows, up to a readable measure: rules under headings span it whatever the text.
        _scroll.SizeChanged += (_, e) => _body.Width = Math.Max(0, Math.Min(MaxTextWidth, e.NewSize.Width - 64 - 12));
        Children.Add(_scroll);
        FilePath = path;
        Load(path);
    }

    /// <summary>Esc / F10 / Alt+F3.</summary>
    public event Action? CloseRequested;

    /// <summary>F3: the same file in the plain viewer.</summary>
    public event Action<string>? ViewRequested;

    /// <summary>F4 / F6: the same file in the editor.</summary>
    public event Action<string>? EditRequested;

    /// <summary>The file shown now (a followed link changes it).</summary>
    public string FilePath { get; private set; }

    public static bool IsMarkdown(string path) => Extensions.Contains(Path.GetExtension(path));

    public void FocusContent() => Focus();

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var modifiers = e.KeyModifiers;
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Escape or Key.F10 when modifiers == KeyModifiers.None:
            case Key.F3 when modifiers == KeyModifiers.Alt:
                CloseRequested?.Invoke();
                break;
            case Key.F3 when modifiers == KeyModifiers.None:
                ViewRequested?.Invoke(FilePath);
                break;
            case Key.F4 or Key.F6 when modifiers == KeyModifiers.None:
                EditRequested?.Invoke(FilePath);
                break;
            case Key.Down:
                ScrollBy(LineStep);
                break;
            case Key.Up:
                ScrollBy(-LineStep);
                break;
            case Key.PageDown or Key.Space:
                ScrollBy(_scroll.Viewport.Height - LineStep);
                break;
            case Key.PageUp:
                ScrollBy(-(_scroll.Viewport.Height - LineStep));
                break;
            case Key.Home when modifiers is KeyModifiers.None or KeyModifiers.Control:
                _scroll.Offset = default;
                break;
            case Key.End when modifiers is KeyModifiers.None or KeyModifiers.Control:
                _scroll.Offset = new Vector(0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height));
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private void ScrollBy(double delta) =>
        _scroll.Offset = new Vector(0, Math.Clamp(_scroll.Offset.Y + delta, 0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height)));

    private void Load(string path)
    {
        FilePath = path;
        _title.Text = path;
        _body.Children.Clear();
        _anchors.Clear();
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _body.Children.Add(Paragraph([new Run(ex.Message) { Foreground = PanelPalette.ErrorForeground }]));
            return;
        }

        foreach (var block in Markdown.Parse(text, Pipeline))
        {
            if (Block(block, 0) is { } control)
            {
                _body.Children.Add(control);
            }
        }

        _scroll.Offset = default;
    }

    // ----- Blocks -----

    private Control? Block(Block block, int depth) => block switch
    {
        HeadingBlock heading => Heading(heading),
        ParagraphBlock paragraph => Paragraph(Inlines(paragraph.Inline)),
        ListBlock list => List(list, depth),
        QuoteBlock quote => Quote(quote, depth),
        FencedCodeBlock or CodeBlock => Code((LeafBlock)block),
        ThematicBreakBlock => new Border { Height = 1, Background = _colors.Border, Margin = new Thickness(0, 12, 0, 12) },
        Table table => TableOf(table),
        ContainerBlock container => Container(container, depth),
        _ => null,
    };

    private StackPanel Container(ContainerBlock container, int depth)
    {
        var panel = new StackPanel();
        foreach (var child in container)
        {
            if (Block(child, depth) is { } control)
            {
                panel.Children.Add(control);
            }
        }

        return panel;
    }

    private Control Heading(HeadingBlock heading)
    {
        var sizes = new[] { 28.0, 22, 18, 16, 14, 13 };
        var block = Paragraph(Inlines(heading.Inline));
        block.FontSize = sizes[Math.Clamp(heading.Level, 1, 6) - 1];
        block.FontWeight = FontWeight.SemiBold;
        block.Margin = new Thickness(0, heading.Level <= 2 ? 18 : 12, 0, 8);
        var anchor = Slug(PlainText(heading.Inline));
        _anchors.TryAdd(anchor, block);
        if (heading.Level <= 2)
        {
            var panel = new StackPanel();
            panel.Children.Add(block);
            panel.Children.Add(new Border { Height = 1, Background = _colors.Border, Margin = new Thickness(0, 0, 0, 8) });
            _anchors[anchor] = panel;
            return panel;
        }

        return block;
    }

    private SelectableTextBlock Paragraph(IEnumerable<Inline> inlines)
    {
        var block = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Foreground = _colors.Foreground,
            Margin = new Thickness(0, 0, 0, 10),
            SelectionBrush = PanelPalette.ActiveSelectionBackground,
        };
        block.Inlines!.AddRange(inlines);
        return block;
    }

    private Grid List(ListBlock list, int depth)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(depth == 0 ? 4 : 0, 0, 0, 6) };
        var number = int.TryParse(list.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ? start : 1;
        var row = 0;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var task = item.Descendants<TaskList>().FirstOrDefault();
            var marker = new TextBlock
            {
                Text = task is not null ? (task.Checked ? "☑" : "☐") : list.IsOrdered ? $"{number++}." : depth % 2 == 0 ? "•" : "◦",
                Foreground = _colors.Muted,
                FontSize = 14,
                Margin = new Thickness(0, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var content = new StackPanel();
            foreach (var child in item)
            {
                if (Block(child, depth + 1) is { } control)
                {
                    if (control is SelectableTextBlock text)
                    {
                        text.Margin = new Thickness(0, 0, 0, 2);
                    }

                    content.Children.Add(control);
                }
            }

            Grid.SetRow(marker, row);
            Grid.SetRow(content, row);
            Grid.SetColumn(content, 1);
            grid.Children.Add(marker);
            grid.Children.Add(content);
            row++;
        }

        return grid;
    }

    private Border Quote(QuoteBlock quote, int depth)
    {
        var inner = Container(quote, depth);
        foreach (var text in inner.Children.OfType<SelectableTextBlock>())
        {
            text.Foreground = _colors.Muted;
        }

        return new Border
        {
            BorderBrush = _colors.Border,
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(12, 2, 0, 2),
            Margin = new Thickness(0, 0, 0, 10),
            Child = inner,
        };
    }

    private Border Code(LeafBlock block)
    {
        var text = new StringBuilder();
        foreach (var line in block.Lines.Lines.Take(block.Lines.Count))
        {
            text.AppendLine(line.Slice.ToString());
        }

        var code = new SelectableTextBlock
        {
            Text = text.ToString().TrimEnd('\r', '\n'),
            FontFamily = new FontFamily(Theming.Fonts.Mono),
            FontSize = 13,
            Foreground = _colors.Foreground,
            SelectionBrush = PanelPalette.ActiveSelectionBackground,
        };
        return new Border
        {
            Background = _colors.CodeBackground,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new ScrollViewer { Content = code, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
    }

    private Border TableOf(Table table)
    {
        var grid = new Grid();
        var columns = table.Max(r => ((TableRow)r).Count);
        for (var i = 0; i < columns; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        var rowIndex = 0;
        foreach (var row in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var column = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                var content = new StackPanel();
                foreach (var child in cell)
                {
                    if (Block(child, 1) is { } control)
                    {
                        if (control is SelectableTextBlock text)
                        {
                            text.Margin = default;
                            text.FontWeight = row.IsHeader ? FontWeight.SemiBold : FontWeight.Normal;
                        }

                        content.Children.Add(control);
                    }
                }

                var border = new Border
                {
                    Child = content,
                    Padding = new Thickness(10, 5, 10, 5),
                    BorderBrush = _colors.Border,
                    BorderThickness = new Thickness(column == 0 ? 1 : 0, rowIndex == 0 ? 1 : 0, 1, 1),
                    Background = rowIndex % 2 == 0 && !row.IsHeader ? _colors.CodeBackground : null,
                };
                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, column);
                grid.Children.Add(border);
                column++;
            }

            rowIndex++;
        }

        return new Border { Child = grid, Margin = new Thickness(0, 0, 0, 12), HorizontalAlignment = HorizontalAlignment.Left };
    }

    // ----- Inlines -----

    private List<Inline> Inlines(ContainerInline? container, TextAttributes attributes = default)
    {
        var result = new List<Inline>();
        if (container is null)
        {
            return result;
        }

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    result.Add(Styled(new Run(literal.Content.ToString()), attributes));
                    break;
                case CodeInline code:
                    result.Add(new Run(code.Content)
                    {
                        FontFamily = new FontFamily(Theming.Fonts.Mono),
                        FontSize = 13,
                        Background = _colors.CodeBackground,
                    });
                    break;
                case EmphasisInline emphasis:
                    var inner = emphasis.DelimiterChar == '~'
                        ? attributes with { Strike = true }
                        : emphasis.DelimiterCount >= 2 ? attributes with { Bold = true } : attributes with { Italic = true };
                    result.AddRange(Inlines(emphasis, inner));
                    break;
                case LinkInline { IsImage: true } image:
                    if (ImageOf(image.Url) is { } picture)
                    {
                        result.Add(new InlineUIContainer(picture));
                    }
                    else
                    {
                        result.Add(Styled(new Run($"[{PlainText(image)}]"), attributes with { Muted = true }));
                    }

                    break;
                case LinkInline link:
                    result.Add(Link(PlainText(link) is { Length: > 0 } label ? label : link.Url ?? string.Empty, link.Url));
                    break;
                case AutolinkInline autolink:
                    result.Add(Link(autolink.Url, autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url));
                    break;
                case LineBreakInline lineBreak:
                    result.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                    break;
                case TaskList:
                    break;
                case ContainerInline nested:
                    result.AddRange(Inlines(nested, attributes));
                    break;
            }
        }

        return result;
    }

    private Run Styled(Run run, TextAttributes attributes)
    {
        if (attributes.Bold)
        {
            run.FontWeight = FontWeight.SemiBold;
        }

        if (attributes.Italic)
        {
            run.FontStyle = FontStyle.Italic;
        }

        if (attributes.Strike)
        {
            run.TextDecorations = TextDecorations.Strikethrough;
        }

        if (attributes.Muted)
        {
            run.Foreground = _colors.Muted;
        }

        return run;
    }

    private InlineUIContainer Link(string text, string? url)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = _colors.Link,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Cursor = Cursors.Hand,
            TextDecorations = TextDecorations.Underline,

            // An inline control sits on the line by its bottom: the descent brings its text down to the baseline.
            Margin = new Thickness(0, 0, 0, -4),
        };
        if (url is not null)
        {
            ToolTip.SetTip(block, url);
        }

        block.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(block).Properties.IsLeftButtonPressed && url is not null)
            {
                e.Handled = true;
                Follow(url);
            }
        };
        return new InlineUIContainer(block);
    }

    private Image? ImageOf(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(FilePath) ?? string.Empty, Uri.UnescapeDataString(url.Split('#', '?')[0])));
            if (!File.Exists(path))
            {
                return null;
            }

            var bitmap = new Bitmap(path);
            return new Image { Source = bitmap, MaxWidth = Math.Min(900, bitmap.Size.Width), Stretch = Stretch.Uniform };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A link: "#anchor" scrolls here, another Markdown file opens here, a web address in the browser, a file in its program.</summary>
    private void Follow(string url)
    {
        if (url.StartsWith('#'))
        {
            ScrollTo(url[1..]);
            return;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" or "mailto")
        {
            App.OpenInBrowser(absolute.AbsoluteUri);
            return;
        }

        var parts = url.Split('#', 2);
        var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(FilePath) ?? string.Empty, Uri.UnescapeDataString(parts[0])));
        if (IsMarkdown(target) && File.Exists(target))
        {
            Load(target);
            if (parts.Length > 1)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ScrollTo(parts[1]), Avalonia.Threading.DispatcherPriority.Loaded);
            }

            return;
        }

        if (File.Exists(target) || Directory.Exists(target))
        {
            App.OpenInBrowser(target);
        }
    }

    private void ScrollTo(string anchor)
    {
        if (_anchors.TryGetValue(Uri.UnescapeDataString(anchor), out var control) && control.TranslatePoint(default, _body) is { } point)
        {
            _scroll.Offset = new Vector(0, Math.Max(0, point.Y - 8));
        }
    }

    private static string PlainText(ContainerInline? container)
    {
        var text = new StringBuilder();
        void Walk(ContainerInline inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        text.Append(literal.Content.ToString());
                        break;
                    case CodeInline code:
                        text.Append(code.Content);
                        break;
                    case ContainerInline nested:
                        Walk(nested);
                        break;
                }
            }
        }

        if (container is not null)
        {
            Walk(container);
        }

        return text.ToString();
    }

    /// <summary>GitHub's heading anchors: lower case, spaces to hyphens, punctuation dropped.</summary>
    private static string Slug(string heading)
    {
        var slug = new StringBuilder();
        foreach (var c in heading.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                slug.Append(c);
            }
            else if (c == ' ')
            {
                slug.Append('-');
            }
        }

        return slug.ToString();
    }

    private readonly record struct TextAttributes(bool Bold, bool Italic, bool Strike, bool Muted);

    /// <summary>The colours the HTML version took from the theme (issue #57).</summary>
    private sealed class Colors(ColorTheme theme)
    {
        public IBrush Background { get; } = Brush(theme, "editor.background");

        public IBrush Foreground { get; } = Brush(theme, "editor.foreground");

        public IBrush Muted { get; } = Brush(theme, "descriptionForeground");

        public IBrush Link { get; } = Brush(theme, "textLink.foreground");

        public IBrush Border { get; } = Brush(theme, "editorWidget.border");

        public IBrush CodeBackground { get; } = Brush(theme, "textCodeBlock.background");

        private static Avalonia.Media.Immutable.ImmutableSolidColorBrush Brush(ColorTheme theme, string key) => new(PanelPalette.ToColor(theme.GetOpaque(key)));
    }
}
