using System.Text.Json;

namespace HabilisTerminal.Themes;

/// <summary>VS Code theme types (the <c>type</c> property; <c>uiTheme</c> in extension manifests).</summary>
public enum ThemeType
{
    Dark,
    Light,
    HighContrast,
    HighContrastLight,
}

/// <summary>
/// A VS Code colour theme after <c>include</c> resolution: workbench colours as written in the files, token colours
/// for the editor, and colour lookup with VS Code-like defaults by theme type (SPEC §10).
/// </summary>
public sealed class ColorTheme
{
    private readonly IReadOnlyDictionary<string, ThemeColor> _colors;

    public ColorTheme(string name, ThemeType type, IReadOnlyDictionary<string, ThemeColor> colors, IReadOnlyList<JsonElement> tokenColors)
    {
        Name = name;
        Type = type;
        _colors = colors;
        TokenColors = tokenColors;
    }

    public string Name { get; }

    public ThemeType Type { get; }

    public bool IsLight => Type is ThemeType.Light or ThemeType.HighContrastLight;

    /// <summary>Colours defined by the theme files themselves (no defaults).</summary>
    public IReadOnlyDictionary<string, ThemeColor> DefinedColors => _colors;

    /// <summary>Raw <c>tokenColors</c> rules (TextMate scopes) for the editor (stage 9).</summary>
    public IReadOnlyList<JsonElement> TokenColors { get; }

    /// <summary>The colour for a workbench key: the theme's own value, else the VS Code default for this theme type.</summary>
    public ThemeColor? Get(string key) => Resolve(key, depth: 0);

    /// <summary>Like <see cref="Get"/>, composited over <paramref name="background"/> (or the editor background) to an opaque colour.</summary>
    public ThemeColor GetOpaque(string key, string backgroundKey = "editor.background")
    {
        var background = Resolve(backgroundKey, 0) ?? ThemeColor.FromRgb(IsLight ? 0xFFFFFF : 0x1E1E1E);
        var color = Resolve(key, 0) ?? background;
        return color.Over(background.A == 255 ? background : background.Over(ThemeColor.FromRgb(IsLight ? 0xFFFFFF : 0x000000)));
    }

    /// <summary>Returns a copy with some colours replaced (e.g. the Windows accent colour).</summary>
    public ColorTheme With(IReadOnlyDictionary<string, ThemeColor> overrides)
    {
        var merged = new Dictionary<string, ThemeColor>(_colors, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, color) in overrides)
        {
            merged[key] = color;
        }

        return new ColorTheme(Name, Type, merged, TokenColors);
    }

    private ThemeColor? Resolve(string key, int depth)
    {
        if (depth > 8)
        {
            return null;
        }

        if (_colors.TryGetValue(key, out var color))
        {
            return color;
        }

        return ThemeDefaults.For(Type).TryGetValue(key, out var fallback)
            ? fallback.Color ?? (fallback.Reference is { } reference ? Resolve(reference, depth + 1) : null)
            : null;
    }
}
