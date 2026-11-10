using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using HabilisTerminal.App.Theming;
using HabilisTerminal.Themes;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars;
using TextMateSharp.Themes;
using TmStyle = TextMateSharp.Themes.FontStyle;

namespace HabilisTerminal.App.Editing;

/// <summary>
/// Syntax colours as in VS Code (issue #207): the TextMate grammars VS Code ships, matched against the colour theme's
/// own <c>tokenColors</c>. Files with no TextMate grammar keep AvalonEdit's definitions, recoloured from the theme.
/// </summary>
internal static class SyntaxColors
{
    /// <summary>AvalonEdit definition names for the extensions TextMate does not know by extension.</summary>
    private static readonly Dictionary<string, string> LanguageOfDefinition = new(StringComparer.OrdinalIgnoreCase)
    {
        ["XML"] = "xml", ["C#"] = "csharp", ["JavaScript"] = "javascript", ["HTML"] = "html", ["CSS"] = "css",
        ["PowerShell"] = "powershell", ["MarkDown"] = "markdown", ["MarkDownWithFontSize"] = "markdown", ["Python"] = "python",
        ["Java"] = "java", ["C++"] = "cpp", ["PHP"] = "php", ["TSQL"] = "sql", ["VB"] = "vb", ["Json"] = "json",
    };

    /// <summary>Turns colours on for <paramref name="path"/>'s language, or off.</summary>
    public static void Set(TextEditor editor, string? path, bool on)
    {
        Off(editor);
        if (!on || path is null)
        {
            return;
        }

        var extension = Path.GetExtension(path);
        var definition = HighlightingManager.Instance.GetDefinitionByExtension(extension);
        if (TextMateSyntax.GrammarFor(extension, LanguageFor(definition?.Name)) is { } grammar)
        {
            editor.TextArea.TextView.LineTransformers.Add(new TextMateColorizer(editor, grammar));
            return;
        }

        editor.SyntaxHighlighting = definition;
    }

    public static bool IsOn(TextEditor editor) => editor.SyntaxHighlighting is not null || Colorizer(editor) is not null;

    public static void Off(TextEditor editor)
    {
        editor.SyntaxHighlighting = null;
        if (Colorizer(editor) is { } colorizer)
        {
            colorizer.Detach();
            editor.TextArea.TextView.LineTransformers.Remove(colorizer);
        }
    }

    /// <summary>The theme changed: TextMate colours follow it.</summary>
    public static void Recolor(TextEditor editor, ColorTheme theme)
    {
        TextMateSyntax.SetTheme(theme);
        if (Colorizer(editor) is { } colorizer)
        {
            colorizer.Invalidate();
            editor.TextArea.TextView.Redraw();
        }
    }

    /// <summary>The scope name TextMate uses for this editor's text, or null; for tests and diagnostics.</summary>
    public static string? ScopeOf(TextEditor editor) => Colorizer(editor)?.ScopeName;

    internal static string? LanguageFor(string? definitionName) =>
        definitionName is not null && LanguageOfDefinition.TryGetValue(definitionName, out var language) ? language : null;

    private static TextMateColorizer? Colorizer(TextEditor editor) =>
        editor.TextArea.TextView.LineTransformers.OfType<TextMateColorizer>().FirstOrDefault();
}


/// <summary>
/// Colours each line from TextMate tokens. A line's tokens depend on the state at the end of the line before, so the
/// end states are kept from the top down; an edit drops those from its line on. Lines far below the last known state
/// are coloured from a fresh state at once, and the states catch up when the window is idle.
/// </summary>
internal sealed class TextMateColorizer : DocumentColorizingTransformer
{
    private static readonly TimeSpan LineTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CatchUpBudget = TimeSpan.FromMilliseconds(30);

    private readonly TextEditor _editor;
    private readonly IGrammar _grammar;
    private readonly List<IStateStack?> _ends = [];
    private TextDocument? _document;
    private int _version = -1;
    private int _checkLine;
    private IStateStack? _checkOld;
    private int _wanted;
    private bool _catchUpQueued;
    private bool _redrawQueued;
    private bool _failed;

    public TextMateColorizer(TextEditor editor, IGrammar grammar)
    {
        _editor = editor;
        _grammar = grammar;
        editor.DocumentChanged += OnEditorDocumentChanged;
        Hook(editor.Document);
    }

    public string ScopeName => _grammar.GetScopeName();

    public void Invalidate()
    {
        _ends.Clear();
        _checkLine = 0;
    }

    public void Detach()
    {
        _editor.DocumentChanged -= OnEditorDocumentChanged;
        Hook(null);
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_failed)
        {
            return;
        }

        if (_version != TextMateSyntax.Version)
        {
            _version = TextMateSyntax.Version;
            Invalidate();
        }

        try
        {
            var document = CurrentContext.Document;
            var number = line.LineNumber;
            IStateStack? start = null;
            var known = CatchUp(document, number - 1, CatchUpBudget);
            if (number > 1)
            {
                if (known)
                {
                    start = _ends[number - 2];
                }
                else
                {
                    // Coloured from a fresh state for now; the right colours come once the states catch up.
                    _wanted = Math.Max(_wanted, number);
                    QueueCatchUp();
                }
            }

            var result = _grammar.TokenizeLine2(document.GetText(line), start, LineTimeout);
            if (known && _ends.Count == number - 1)
            {
                _ends.Add(result.RuleStack);
                if (_checkLine == number)
                {
                    // The edited line ends in another state (an opened comment, say): the lines below change too.
                    if (!Equals(_checkOld, result.RuleStack))
                    {
                        QueueRedraw();
                    }

                    _checkLine = 0;
                }
            }

            Paint(line, result.Tokens);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning($"TextMate colouring failed: {ex.Message}");
            _failed = true;
        }
    }

    private static Brush?[] s_brushes = [];
    private static int s_brushVersion = -1;

    /// <summary>Brushes by TextMate colour id for the current theme.</summary>
    private static Brush?[] Brushes()
    {
        if (s_brushVersion != TextMateSyntax.Version)
        {
            s_brushes = [.. TextMateSyntax.Colors.Select(color =>
            {
                if (color is not { } c)
                {
                    return (Brush?)null;
                }

                var brush = new SolidColorBrush(PanelPalette.ToColor(c));
                brush.Freeze();
                return brush;
            })];
            s_brushVersion = TextMateSyntax.Version;
        }

        return s_brushes;
    }

    private void Paint(DocumentLine line, int[] tokens)
    {
        var brushes = Brushes();
        for (var i = 0; i < tokens.Length; i += 2)
        {
            var start = Math.Min(tokens[i], line.Length);
            var end = i + 2 < tokens.Length ? Math.Min(tokens[i + 2], line.Length) : line.Length;
            if (end <= start)
            {
                continue;
            }

            var metadata = tokens[i + 1];
            var foreground = EncodedTokenAttributes.GetForeground(metadata);
            var style = EncodedTokenAttributes.GetFontStyle(metadata);
            var brush = foreground > 1 && foreground < brushes.Length ? brushes[foreground] : null;
            if (brush is null && style is TmStyle.None or TmStyle.NotSet)
            {
                continue;
            }

            ChangeLinePart(line.Offset + start, line.Offset + end, element =>
            {
                if (brush is not null)
                {
                    element.TextRunProperties.SetForegroundBrush(brush);
                }

                if (style is TmStyle.None or TmStyle.NotSet)
                {
                    return;
                }

                var typeface = element.TextRunProperties.Typeface;
                if (style.HasFlag(TmStyle.Italic) || style.HasFlag(TmStyle.Bold))
                {
                    element.TextRunProperties.SetTypeface(new Typeface(
                        typeface.FontFamily,
                        style.HasFlag(TmStyle.Italic) ? FontStyles.Italic : typeface.Style,
                        style.HasFlag(TmStyle.Bold) ? FontWeights.Bold : typeface.Weight,
                        typeface.Stretch));
                }

                if (style.HasFlag(TmStyle.Underline) || style.HasFlag(TmStyle.Strikethrough))
                {
                    var decorations = new TextDecorationCollection();
                    if (style.HasFlag(TmStyle.Underline))
                    {
                        decorations.Add(TextDecorations.Underline);
                    }

                    if (style.HasFlag(TmStyle.Strikethrough))
                    {
                        decorations.Add(TextDecorations.Strikethrough);
                    }

                    element.TextRunProperties.SetTextDecorations(decorations);
                }
            });
        }
    }

    /// <summary>Works out end states down to <paramref name="lines"/> lines within the budget; true when they are all known.</summary>
    private bool CatchUp(TextDocument document, int lines, TimeSpan budget)
    {
        lines = Math.Min(lines, document.LineCount);
        if (_ends.Count >= lines)
        {
            return true;
        }

        var clock = Stopwatch.StartNew();
        while (_ends.Count < lines)
        {
            var line = document.GetLineByNumber(_ends.Count + 1);
            var start = _ends.Count == 0 ? null : _ends[^1];
            _ends.Add(_grammar.TokenizeLine2(document.GetText(line), start, LineTimeout).RuleStack);
            if (clock.Elapsed > budget && _ends.Count < lines)
            {
                return false;
            }
        }

        return true;
    }

    private void QueueCatchUp()
    {
        if (_catchUpQueued)
        {
            return;
        }

        _catchUpQueued = true;
        _editor.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            _catchUpQueued = false;
            if (_failed || _document is null || _version != TextMateSyntax.Version)
            {
                return;
            }

            try
            {
                if (CatchUp(_document, Math.Min(_wanted, _document.LineCount) - 1, CatchUpBudget))
                {
                    _wanted = 0;
                    _editor.TextArea.TextView.Redraw();
                }
                else
                {
                    QueueCatchUp();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Trace.TraceWarning($"TextMate colouring failed: {ex.Message}");
                _failed = true;
            }
        });
    }

    private void QueueRedraw()
    {
        if (_redrawQueued)
        {
            return;
        }

        _redrawQueued = true;
        _editor.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _redrawQueued = false;
            _editor.TextArea.TextView.Redraw();
        });
    }

    private void OnEditorDocumentChanged(object? sender, EventArgs e)
    {
        Hook(_editor.Document);
        Invalidate();
    }

    private void Hook(TextDocument? document)
    {
        if (_document is not null)
        {
            _document.Changed -= OnChanged;
        }

        _document = document;
        if (document is not null)
        {
            document.Changed += OnChanged;
        }
    }

    private void OnChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        var number = _document.GetLineByOffset(Math.Min(e.Offset, _document.TextLength)).LineNumber;
        if (number - 1 < _ends.Count)
        {
            _checkOld = _ends[number - 1];
            _ends.RemoveRange(number - 1, _ends.Count - (number - 1));
        }
        else
        {
            _checkOld = null;
        }

        _checkLine = number;
        if (e.RemovedText.IndexOf('\n', 0, e.RemovedText.TextLength) >= 0 || e.InsertedText.IndexOf('\n', 0, e.InsertedText.TextLength) >= 0)
        {
            // Lines moved: AvalonEdit does not colour the ones below again by itself.
            QueueRedraw();
        }
    }
}
