using System.Text.Json;
using System.Windows.Media;
using HabilisTerminal.App.Theming;
using HabilisTerminal.Themes;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace HabilisTerminal.App.Editing;

/// <summary>
/// AvalonEdit colours from the colour theme (<c>editor.*</c>) and syntax colours from its <c>tokenColors</c>; shared by
/// the editor (F4) and the viewer's text mode (F3, issue #56). TextMate grammars colour most files
/// (<see cref="SyntaxColors"/>, issue #207); the rest keep AvalonEdit's definitions, recoloured here.
/// </summary>
internal static class TextEditorTheme
{
    public static Brush Brush(ColorTheme theme, string key, string fallbackKey = "editor.background")
    {
        var background = theme.GetOpaque("editor.background");
        var color = (theme.Get(key) ?? theme.Get(fallbackKey) ?? background).Over(background);
        var brush = new SolidColorBrush(PanelPalette.ToColor(color));
        brush.Freeze();
        return brush;
    }

    public static void Apply(TextEditor editor, ColorTheme theme)
    {
        editor.Background = Brush(theme, "editor.background");
        editor.Foreground = Brush(theme, "editor.foreground", "foreground");
        editor.LineNumbersForeground = Brush(theme, "editorLineNumber.foreground", "editor.foreground");
        editor.TextArea.SelectionBrush = Brush(theme, "editor.selectionBackground");
        editor.TextArea.SelectionForeground = null;
        editor.TextArea.SelectionBorder = null;
        editor.TextArea.Caret.CaretBrush = Brush(theme, "editorCursor.foreground", "editor.foreground");
        editor.TextArea.TextView.CurrentLineBackground = Brush(theme, "editor.lineHighlightBackground");
        editor.TextArea.TextView.CurrentLineBorder = new Pen(Brush(theme, "editor.lineHighlightBorder", "editor.lineHighlightBackground"), 1);
        RecolorSyntax(editor, theme);
        SyntaxColors.Recolor(editor, theme);
    }

    private static void RecolorSyntax(TextEditor editor, ColorTheme theme)
    {
        if (editor.SyntaxHighlighting is not { } definition)
        {
            return;
        }

        var scopes = TokenColors(theme);
        Color? Scope(params string[] candidates)
        {
            // TextMate matching: a rule applies when its scope is a prefix of the token scope; the longest wins.
            foreach (var candidate in candidates)
            {
                Color? best = null;
                var bestLength = -1;
                foreach (var (scope, color) in scopes)
                {
                    if (scope.Length > bestLength && (scope == candidate || candidate.StartsWith(scope + ".", StringComparison.Ordinal)))
                    {
                        best = color;
                        bestLength = scope.Length;
                    }
                }

                if (best is not null)
                {
                    return best;
                }
            }

            return null;
        }

        foreach (var named in definition.NamedHighlightingColors)
        {
            var name = named.Name;
            var color = name.Contains("Punctuation", StringComparison.OrdinalIgnoreCase) ? null
                : name.Contains("Comment", StringComparison.OrdinalIgnoreCase) ? Scope("comment")
                : name.Contains("TrueFalse", StringComparison.OrdinalIgnoreCase) || name.Contains("Null", StringComparison.OrdinalIgnoreCase) ? Scope("constant.language")
                : name.Contains("ThisOrBase", StringComparison.OrdinalIgnoreCase) ? Scope("variable.language", "keyword")
                : name.Contains("String", StringComparison.OrdinalIgnoreCase) || name.Contains("Char", StringComparison.OrdinalIgnoreCase) ? Scope("string")
                : name.Contains("Number", StringComparison.OrdinalIgnoreCase) || name.Contains("Digits", StringComparison.OrdinalIgnoreCase) ? Scope("constant.numeric")
                : name.Contains("Method", StringComparison.OrdinalIgnoreCase) || name.Contains("Function", StringComparison.OrdinalIgnoreCase) ? Scope("entity.name.function")
                : name.Contains("Attribute", StringComparison.OrdinalIgnoreCase) ? Scope("entity.other.attribute-name")
                : name.Contains("Tag", StringComparison.OrdinalIgnoreCase) || name.Contains("Element", StringComparison.OrdinalIgnoreCase) ? Scope("entity.name.tag")
                : name.Contains("Type", StringComparison.OrdinalIgnoreCase) ? Scope("entity.name.type", "storage.type")
                : name.Contains("Preprocessor", StringComparison.OrdinalIgnoreCase) ? Scope("keyword.control.directive", "meta.preprocessor", "keyword")
                : name is "Keywords" || name.Contains("Goto", StringComparison.OrdinalIgnoreCase) || name.Contains("Exception", StringComparison.OrdinalIgnoreCase) ? Scope("keyword.control", "keyword")
                : Scope("storage.modifier", "keyword.other", "keyword");
            named.Foreground = color is { } c ? new SimpleHighlightingBrush(c) : new SimpleHighlightingBrush(((SolidColorBrush)editor.Foreground).Color);
            named.Background = null;
            named.FontWeight = null;
            named.FontStyle = null;
        }

        // Force AvalonEdit to re-apply the changed colours.
        editor.SyntaxHighlighting = null;
        editor.SyntaxHighlighting = definition;
    }

    private static List<(string Scope, Color Color)> TokenColors(ColorTheme theme)
    {
        var result = new List<(string, Color)>();
        foreach (var rule in theme.TokenColors)
        {
            if (!rule.TryGetProperty("settings", out var settings) || !settings.TryGetProperty("foreground", out var foreground) ||
                !ThemeColor.TryParse(foreground.GetString(), out var color))
            {
                continue;
            }

            if (!rule.TryGetProperty("scope", out var scope))
            {
                continue;
            }

            IEnumerable<string> names = scope.ValueKind == JsonValueKind.Array
                ? scope.EnumerateArray().Select(s => s.GetString() ?? string.Empty)
                : (scope.GetString() ?? string.Empty).Split(',');
            foreach (var name in names)
            {
                result.Add((name.Trim(), PanelPalette.ToColor(color)));
            }
        }

        // Later rules win in TextMate themes.
        result.Reverse();
        return result;
    }
}
