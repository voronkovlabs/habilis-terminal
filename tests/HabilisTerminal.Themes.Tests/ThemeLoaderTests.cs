namespace HabilisTerminal.Themes.Tests;

public class ThemeLoaderTests
{
    /// <summary>Text and its background as the app draws them; each must be readable in every built-in theme (issue #109).</summary>
    public static TheoryData<string, string, string> ColourPairs()
    {
        (string Text, string Background)[] pairs =
        [
            ("titleBar.activeForeground", "titleBar.activeBackground"),
            ("sideBar.foreground", "sideBar.background"),
            ("descriptionForeground", "sideBar.background"),
            ("list.activeSelectionForeground", "list.activeSelectionBackground"),
            ("input.foreground", "input.background"),
            ("quickInput.foreground", "quickInput.background"),
            ("button.foreground", "button.background"),
            ("button.secondaryForeground", "button.secondaryBackground"),
            ("terminal.foreground", "terminal.background"),
            ("editor.foreground", "editor.background"),
        ];
        var data = new TheoryData<string, string, string>();
        foreach (var theme in ThemeLoader.BuiltInThemes)
        {
            foreach (var (text, background) in pairs)
            {
                data.Add(theme, text, background);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ColourPairs))]
    public void Every_built_in_theme_keeps_text_readable(string themeName, string text, string background)
    {
        var theme = ThemeLoader.LoadBuiltIn(themeName);
        var under = theme.GetOpaque(background, "sideBar.background");
        var over = theme.Get(text);
        Assert.NotNull(over);
        var (a, b) = (Luminance(over.Value.Over(under)), Luminance(under));
        var contrast = (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        Assert.True(contrast >= 3, $"{themeName}: {text} on {background} has contrast {contrast:0.0}");
    }

    private static double Luminance(ThemeColor color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    [Theory]
    [InlineData("#abc", 0xAA, 0xBB, 0xCC, 0xFF)]
    [InlineData("#abcd", 0xAA, 0xBB, 0xCC, 0xDD)]
    [InlineData("#123456", 0x12, 0x34, 0x56, 0xFF)]
    [InlineData("#12345678", 0x12, 0x34, 0x56, 0x78)]
    public void Parses_vscode_colour_notation(string text, int r, int g, int b, int a)
    {
        Assert.Equal(new ThemeColor((byte)r, (byte)g, (byte)b, (byte)a), ThemeColor.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("#12")]
    [InlineData("#zzzzzz")]
    public void Rejects_invalid_colours(string text)
    {
        Assert.False(ThemeColor.TryParse(text, out _));
    }

    [Fact]
    public void Built_in_themes_are_embedded()
    {
        Assert.Contains(ThemeLoader.DarkModern, ThemeLoader.BuiltInThemes);
        Assert.Contains(ThemeLoader.LightModern, ThemeLoader.BuiltInThemes);
        Assert.Contains(ThemeLoader.HighContrastDark, ThemeLoader.BuiltInThemes);
    }

    [Fact]
    public void Dark_modern_resolves_its_include_chain()
    {
        var theme = ThemeLoader.LoadBuiltIn(ThemeLoader.DarkModern);

        Assert.Equal("Dark Modern", theme.Name);
        Assert.Equal(ThemeType.Dark, theme.Type);

        // Own value (dark_modern), inherited value (dark_vs) and token rules from the whole chain.
        Assert.Equal(ThemeColor.Parse("#181818"), theme.Get("sideBar.background"));
        Assert.NotNull(theme.Get("editor.background"));
        Assert.NotEmpty(theme.TokenColors);
    }

    [Fact]
    public void Light_modern_is_light_and_has_terminal_colours()
    {
        var theme = ThemeLoader.LoadBuiltIn(ThemeLoader.LightModern);

        Assert.True(theme.IsLight);
        Assert.All(ThemeDefaults.TerminalAnsiKeys, key => Assert.NotNull(theme.Get(key)));
    }

    [Fact]
    public void Missing_keys_fall_back_to_defaults_by_type()
    {
        var dark = ThemeLoader.Load("t.json", _ => """{ "type": "dark", "colors": { "focusBorder": "#FF0000" } }""");
        var light = ThemeLoader.Load("t.json", _ => """{ "type": "light", "colors": {} }""");

        Assert.Equal(ThemeColor.Parse("#252526"), dark.Get("sideBar.background"));
        Assert.Equal(ThemeColor.Parse("#F3F3F3"), light.Get("sideBar.background"));

        // References follow the theme's own overrides: list.focusOutline -> focusBorder.
        Assert.Equal(ThemeColor.Parse("#FF0000"), dark.Get("list.focusOutline"));
        Assert.Null(dark.Get("no.such.key"));
    }

    [Fact]
    public void Including_theme_wins_and_jsonc_is_accepted()
    {
        var files = new Dictionary<string, string>
        {
            ["main.json"] = """
                // comment
                { "name": "Main", "include": "./base/base.json", "colors": { "a": "#111111", }, "tokenColors": [ { "scope": "x" } ] }
                """,
            ["base/base.json"] = """{ "type": "light", "colors": { "a": "#222222", "b": "#333333" }, "tokenColors": [ { "scope": "y" } ] }""",
        };

        var theme = ThemeLoader.Load("main.json", name => files[name]);

        Assert.Equal(ThemeType.Light, theme.Type);
        Assert.Equal(ThemeColor.Parse("#111111"), theme.Get("a"));
        Assert.Equal(ThemeColor.Parse("#333333"), theme.Get("b"));
        Assert.Equal(["y", "x"], theme.TokenColors.Select(t => t.GetProperty("scope").GetString()));
    }

    [Fact]
    public void Include_cycles_and_bad_json_are_reported()
    {
        Assert.Throws<ThemeLoadException>(() => ThemeLoader.Load("a.json", name => name == "a.json" ? """{ "include": "b.json" }""" : """{ "include": "a.json" }"""));
        Assert.Throws<ThemeLoadException>(() => ThemeLoader.Load("a.json", _ => "{ not json"));
    }

    [Fact]
    public void Type_is_guessed_from_background_when_missing()
    {
        var theme = ThemeLoader.Load("t.json", _ => """{ "colors": { "editor.background": "#FAFAFA" } }""");

        Assert.Equal(ThemeType.Light, theme.Type);
    }

    [Fact]
    public void Accent_tints_focus_and_selection_but_not_high_contrast()
    {
        var accent = ThemeColor.Parse("#E81123");
        var dark = WindowsAppearance.ApplyAccent(ThemeLoader.LoadBuiltIn(ThemeLoader.DarkModern), accent);
        var contrast = ThemeLoader.LoadBuiltIn(ThemeLoader.HighContrastDark);

        Assert.Equal(accent, dark.Get("focusBorder"));
        Assert.Equal(accent, dark.Get("list.focusOutline"));
        Assert.NotEqual(ThemeColor.Parse("#04395E"), dark.Get("list.activeSelectionBackground"));
        Assert.Same(contrast, WindowsAppearance.ApplyAccent(contrast, accent));
    }

    [Fact]
    public void Theme_files_load_from_disk_with_relative_includes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "habi-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "my.json"), """{ "include": "./base.json", "colors": { "a": "#010203" } }""");
            File.WriteAllText(Path.Combine(directory, "base.json"), """{ "type": "dark", "colors": { "b": "#040506" } }""");

            var theme = ThemeLoader.LoadFile(Path.Combine(directory, "my.json"));

            Assert.Equal(ThemeColor.Parse("#010203"), theme.Get("a"));
            Assert.Equal(ThemeColor.Parse("#040506"), theme.Get("b"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
