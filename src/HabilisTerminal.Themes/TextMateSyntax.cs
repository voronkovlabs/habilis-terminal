using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace HabilisTerminal.Themes;

/// <summary>
/// Syntax colours as in VS Code (issue #207): the TextMate grammars VS Code ships (TextMateSharp.Grammars), coloured by
/// the colour theme's own <c>tokenColors</c> with TextMate's selector matching.
/// <remarks>
/// One registry for the whole app: grammars are compiled once, and the colour theme is the app's (all editors show the
/// same theme). The registry's colour ids change with the theme, so tokens are worked out again after a change.
/// </remarks>
public static class TextMateSyntax
{
    private static readonly Dictionary<string, IGrammar?> Grammars = new(StringComparer.Ordinal);
    private static RegistryOptions? s_grammarOptions;
    private static Options? s_options;
    private static Registry? s_registry;
    private static (IReadOnlyList<JsonElement> Tokens, ThemeColor Foreground, ThemeColor Background)? s_themeKey;
    private static bool s_broken;

    /// <summary>Bumped on every theme change; a colorizer seeing a new value drops its tokens.</summary>
    public static int Version { get; private set; }

    /// <summary>Colours by TextMate colour id, opaque over the editor background.</summary>
    public static IReadOnlyList<ThemeColor?> Colors { get; private set; } = [];

    /// <summary>The grammar for a file extension, else for a VS Code language id (<c>csharp</c>, <c>xml</c>); null when none.</summary>
    public static IGrammar? GrammarFor(string extension, string? languageId = null)
    {
        if (s_broken)
        {
            return null;
        }

        try
        {
            EnsureRegistry(null);
            var scope = string.IsNullOrEmpty(extension) ? null : s_grammarOptions!.GetScopeByExtension(extension);
            if (scope is null && languageId is not null)
            {
                scope = s_grammarOptions!.GetScopeByLanguageId(languageId);
            }

            if (scope is null)
            {
                return null;
            }

            if (!Grammars.TryGetValue(scope, out var grammar))
            {
                grammar = s_registry!.LoadGrammar(scope);
                Grammars[scope] = grammar;
            }

            return grammar;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning($"TextMate grammar for '{extension}' failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Uses the theme's <c>tokenColors</c> from now on; does nothing when they have not changed.</summary>
    public static void SetTheme(ColorTheme theme)
    {
        var foreground = theme.GetOpaque("editor.foreground");
        var background = theme.GetOpaque("editor.background");
        if (s_broken || (s_themeKey is { } key && ReferenceEquals(key.Tokens, theme.TokenColors) && key.Foreground == foreground && key.Background == background))
        {
            return;
        }

        try
        {
            var raw = RawTheme(theme, foreground, background);
            if (!EnsureRegistry(raw))
            {
                s_options!.Theme = raw;
                s_registry!.SetTheme(raw);
            }

            // Colour ids start at 1, in the order the colour map lists them.
            var colors = new List<ThemeColor?> { null };
            foreach (var text in s_registry!.GetColorMap())
            {
                colors.Add(ThemeColor.TryParse(text, out var color) ? color.Over(background) : null);
            }

            Colors = colors;
            s_themeKey = (theme.TokenColors, foreground, background);
            Version++;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A broken native regex library or theme must not take the editor down: AvalonEdit's colours stay.
            Trace.TraceWarning($"TextMate setup failed: {ex.Message}");
            s_broken = true;
        }
    }

    /// <summary>Creates the registry once, with <paramref name="raw"/> or an empty theme; true when created now.</summary>
    private static bool EnsureRegistry(IRawTheme? raw)
    {
        if (s_registry is not null)
        {
            return false;
        }

        raw ??= Read("{\"tokenColors\":[]}"u8.ToArray());
        s_grammarOptions = new RegistryOptions(ThemeName.DarkPlus);
        s_options = new Options(s_grammarOptions, raw);
        s_registry = new Registry(s_options);
        return true;
    }

    private static IRawTheme Read(byte[] json)
    {
        using var reader = new StreamReader(new MemoryStream(json), Encoding.UTF8);
        return ThemeReader.ReadThemeSync(reader);
    }

    /// <summary>The theme's token rules as a TextMate theme, with the editor's colours as the default rule.</summary>
    private static IRawTheme RawTheme(ColorTheme theme, ThemeColor foreground, ThemeColor background)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("name", theme.Name);
            writer.WriteStartArray("tokenColors");
            writer.WriteStartObject();
            writer.WriteStartObject("settings");
            writer.WriteString("foreground", Hex(foreground));
            writer.WriteString("background", Hex(background));
            writer.WriteEndObject();
            writer.WriteEndObject();
            foreach (var rule in theme.TokenColors)
            {
                rule.WriteTo(writer);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Read(stream.ToArray());
    }

    private static string Hex(ThemeColor color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Tokens of consecutive lines as (start, end, colour, style); for tests and diagnostics.</summary>
    public static List<(int Line, int Start, int End, ThemeColor? Color, FontStyle Style)> Tokenize(IGrammar grammar, IEnumerable<string> lines)
    {
        var result = new List<(int, int, int, ThemeColor?, FontStyle)>();
        IStateStack? state = null;
        var number = 0;
        foreach (var line in lines)
        {
            number++;
            var tokens = grammar.TokenizeLine2(line, state, TimeSpan.FromSeconds(5));
            state = tokens.RuleStack;
            for (var i = 0; i < tokens.Tokens.Length; i += 2)
            {
                var end = i + 2 < tokens.Tokens.Length ? tokens.Tokens[i + 2] : line.Length;
                var metadata = tokens.Tokens[i + 1];
                var id = EncodedTokenAttributes.GetForeground(metadata);
                result.Add((number, tokens.Tokens[i], end, id < Colors.Count ? Colors[id] : null, EncodedTokenAttributes.GetFontStyle(metadata)));
            }
        }

        return result;
    }

    private sealed class Options(RegistryOptions grammars, IRawTheme theme) : IRegistryOptions
    {
        public IRawTheme Theme { get; set; } = theme;

        public IRawTheme GetTheme(string scopeName) => Theme;

        public IRawGrammar GetGrammar(string scopeName) => grammars.GetGrammar(scopeName);

        public ICollection<string> GetInjections(string scopeName) => grammars.GetInjections(scopeName);

        public IRawTheme GetDefaultTheme() => Theme;
    }
}