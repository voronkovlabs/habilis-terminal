namespace HabilisTerminal.Themes;

/// <summary>A default: a fixed colour, or a reference to another key (VS Code's colour registry works the same way).</summary>
public readonly record struct DefaultColor(ThemeColor? Color, string? Reference);

/// <summary>
/// VS Code workbench defaults for the keys Habilis Terminal uses, per theme type (from VS Code's colour registry),
/// so that themes which only set a few colours still produce a complete palette.
/// </summary>
public static class ThemeDefaults
{
    private static readonly IReadOnlyDictionary<string, DefaultColor> Dark = Build(
        ("foreground", "#CCCCCC"), ("descriptionForeground", "#CCCCCCB3"), ("errorForeground", "#F48771"),
        ("focusBorder", "#007FD4"), ("icon.foreground", "#C5C5C5"), ("widget.border", "#454545"),
        ("editor.background", "#1E1E1E"), ("editor.foreground", "#D4D4D4"), ("editor.selectionBackground", "#264F78"),
        ("editorCursor.foreground", "#AEAFAD"), ("editorLineNumber.foreground", "#858585"),
        ("editorWidget.background", "#252526"), ("editorWidget.border", "#454545"),
        ("sideBar.background", "#252526"), ("sideBar.foreground", "=foreground"), ("sideBar.border", "#00000000"),
        ("sideBarSectionHeader.background", "#00000000"), ("sideBarSectionHeader.foreground", "=sideBar.foreground"),
        ("list.activeSelectionBackground", "#04395E"), ("list.activeSelectionForeground", "#FFFFFF"),
        ("list.inactiveSelectionBackground", "#37373D"), ("list.hoverBackground", "#2A2D2E"),
        ("list.focusOutline", "=focusBorder"), ("list.highlightForeground", "#2AAAFF"),
        ("input.background", "#3C3C3C"), ("input.foreground", "=foreground"), ("input.border", "#00000000"),
        ("input.placeholderForeground", "#A6A6A6"), ("quickInput.background", "=editorWidget.background"),
        ("quickInput.foreground", "=foreground"), ("progressBar.background", "#0E70C0"),
        ("gitDecoration.modifiedResourceForeground", "#E2C08D"), ("scrollbarSlider.background", "#79797966"),
        ("panel.background", "=editor.background"), ("panel.border", "#80808059"),
        ("terminal.background", "=panel.background"), ("terminal.foreground", "#CCCCCC"),
        ("terminal.selectionBackground", "=editor.selectionBackground"), ("terminalCursor.foreground", "=editorCursor.foreground"),
        ("terminal.ansiBlack", "#000000"), ("terminal.ansiRed", "#CD3131"), ("terminal.ansiGreen", "#0DBC79"),
        ("terminal.ansiYellow", "#E5E510"), ("terminal.ansiBlue", "#2472C8"), ("terminal.ansiMagenta", "#BC3FBC"),
        ("terminal.ansiCyan", "#11A8CD"), ("terminal.ansiWhite", "#E5E5E5"), ("terminal.ansiBrightBlack", "#666666"),
        ("terminal.ansiBrightRed", "#F14C4C"), ("terminal.ansiBrightGreen", "#23D18B"), ("terminal.ansiBrightYellow", "#F5F543"),
        ("terminal.ansiBrightBlue", "#3B8EEA"), ("terminal.ansiBrightMagenta", "#D670D6"), ("terminal.ansiBrightCyan", "#29B8DB"),
        ("terminal.ansiBrightWhite", "#E5E5E5"));

    private static readonly IReadOnlyDictionary<string, DefaultColor> Light = Build(
        ("foreground", "#616161"), ("descriptionForeground", "#717171"), ("errorForeground", "#A1260D"),
        ("focusBorder", "#0090F1"), ("icon.foreground", "#424242"), ("widget.border", "#C8C8C8"),
        ("editor.background", "#FFFFFF"), ("editor.foreground", "#000000"), ("editor.selectionBackground", "#ADD6FF"),
        ("editorCursor.foreground", "#000000"), ("editorLineNumber.foreground", "#237893"),
        ("editorWidget.background", "#F3F3F3"), ("editorWidget.border", "#C8C8C8"),
        ("sideBar.background", "#F3F3F3"), ("sideBar.foreground", "=foreground"), ("sideBar.border", "#00000000"),
        ("sideBarSectionHeader.background", "#00000000"), ("sideBarSectionHeader.foreground", "=sideBar.foreground"),
        ("list.activeSelectionBackground", "#0060C0"), ("list.activeSelectionForeground", "#FFFFFF"),
        ("list.inactiveSelectionBackground", "#E4E6F1"), ("list.hoverBackground", "#F0F0F0"),
        ("list.focusOutline", "=focusBorder"), ("list.highlightForeground", "#0066BF"),
        ("input.background", "#FFFFFF"), ("input.foreground", "=foreground"), ("input.border", "#CECECE"),
        ("input.placeholderForeground", "#767676"), ("quickInput.background", "=editorWidget.background"),
        ("quickInput.foreground", "=foreground"), ("progressBar.background", "#0E70C0"),
        ("gitDecoration.modifiedResourceForeground", "#895503"), ("scrollbarSlider.background", "#64646466"),
        ("panel.background", "=editor.background"), ("panel.border", "#80808059"),
        ("terminal.background", "=panel.background"), ("terminal.foreground", "#333333"),
        ("terminal.selectionBackground", "=editor.selectionBackground"), ("terminalCursor.foreground", "=editorCursor.foreground"),
        ("terminal.ansiBlack", "#000000"), ("terminal.ansiRed", "#CD3131"), ("terminal.ansiGreen", "#107C10"),
        ("terminal.ansiYellow", "#949800"), ("terminal.ansiBlue", "#0451A5"), ("terminal.ansiMagenta", "#BC05BC"),
        ("terminal.ansiCyan", "#0598BC"), ("terminal.ansiWhite", "#555555"), ("terminal.ansiBrightBlack", "#666666"),
        ("terminal.ansiBrightRed", "#CD3131"), ("terminal.ansiBrightGreen", "#14CE14"), ("terminal.ansiBrightYellow", "#B5BA00"),
        ("terminal.ansiBrightBlue", "#0451A5"), ("terminal.ansiBrightMagenta", "#BC05BC"), ("terminal.ansiBrightCyan", "#0598BC"),
        ("terminal.ansiBrightWhite", "#A5A5A5"));

    private static readonly IReadOnlyDictionary<string, DefaultColor> HighContrast = Build(
        ("foreground", "#FFFFFF"), ("descriptionForeground", "#FFFFFFB3"), ("errorForeground", "#F48771"),
        ("focusBorder", "#F38518"), ("contrastBorder", "#6FC3DF"), ("icon.foreground", "#FFFFFF"), ("widget.border", "=contrastBorder"),
        ("editor.background", "#000000"), ("editor.foreground", "#FFFFFF"), ("editor.selectionBackground", "#FFFFFF"),
        ("editorCursor.foreground", "#FFFFFF"), ("editorLineNumber.foreground", "#FFFFFF"),
        ("editorWidget.background", "#0C141F"), ("editorWidget.border", "=contrastBorder"),
        ("sideBar.background", "#000000"), ("sideBar.foreground", "=foreground"), ("sideBar.border", "=contrastBorder"),
        ("sideBarSectionHeader.background", "#00000000"), ("sideBarSectionHeader.foreground", "=sideBar.foreground"),
        ("list.activeSelectionBackground", "#00000000"), ("list.activeSelectionForeground", "#FFFFFF"),
        ("list.inactiveSelectionBackground", "#00000000"), ("list.hoverBackground", "#00000000"),
        ("list.focusOutline", "=focusBorder"), ("list.highlightForeground", "#F38518"),
        ("input.background", "#000000"), ("input.foreground", "=foreground"), ("input.border", "=contrastBorder"),
        ("input.placeholderForeground", "#FFFFFFB3"), ("quickInput.background", "=editorWidget.background"),
        ("quickInput.foreground", "=foreground"), ("progressBar.background", "=contrastBorder"),
        ("gitDecoration.modifiedResourceForeground", "#E2C08D"), ("scrollbarSlider.background", "#6FC3DF99"),
        ("panel.background", "=editor.background"), ("panel.border", "=contrastBorder"),
        ("terminal.background", "=panel.background"), ("terminal.foreground", "#FFFFFF"),
        ("terminal.selectionBackground", "=editor.selectionBackground"), ("terminalCursor.foreground", "=editorCursor.foreground"),
        ("terminal.ansiBlack", "#000000"), ("terminal.ansiRed", "#CD0000"), ("terminal.ansiGreen", "#00CD00"),
        ("terminal.ansiYellow", "#CDCD00"), ("terminal.ansiBlue", "#0000EE"), ("terminal.ansiMagenta", "#CD00CD"),
        ("terminal.ansiCyan", "#00CDCD"), ("terminal.ansiWhite", "#E5E5E5"), ("terminal.ansiBrightBlack", "#7F7F7F"),
        ("terminal.ansiBrightRed", "#FF0000"), ("terminal.ansiBrightGreen", "#00FF00"), ("terminal.ansiBrightYellow", "#FFFF00"),
        ("terminal.ansiBrightBlue", "#5C5CFF"), ("terminal.ansiBrightMagenta", "#FF00FF"), ("terminal.ansiBrightCyan", "#00FFFF"),
        ("terminal.ansiBrightWhite", "#FFFFFF"));

    private static readonly IReadOnlyDictionary<string, DefaultColor> HighContrastLight = Build(
        ("foreground", "#292929"), ("descriptionForeground", "#292929B3"), ("errorForeground", "#B5200D"),
        ("focusBorder", "#006BBD"), ("contrastBorder", "#0F4A85"), ("icon.foreground", "#292929"), ("widget.border", "=contrastBorder"),
        ("editor.background", "#FFFFFF"), ("editor.foreground", "#292929"), ("editor.selectionBackground", "#0F4A85"),
        ("editorCursor.foreground", "#0F4A85"), ("editorLineNumber.foreground", "#292929"),
        ("editorWidget.background", "#FFFFFF"), ("editorWidget.border", "=contrastBorder"),
        ("sideBar.background", "#FFFFFF"), ("sideBar.foreground", "=foreground"), ("sideBar.border", "=contrastBorder"),
        ("sideBarSectionHeader.background", "#00000000"), ("sideBarSectionHeader.foreground", "=sideBar.foreground"),
        ("list.activeSelectionBackground", "#0F4A851A"), ("list.activeSelectionForeground", "#292929"),
        ("list.inactiveSelectionBackground", "#0F4A851A"), ("list.hoverBackground", "#0F4A851A"),
        ("list.focusOutline", "=focusBorder"), ("list.highlightForeground", "#0F4A85"),
        ("input.background", "#FFFFFF"), ("input.foreground", "=foreground"), ("input.border", "=contrastBorder"),
        ("input.placeholderForeground", "#292929B3"), ("quickInput.background", "=editorWidget.background"),
        ("quickInput.foreground", "=foreground"), ("progressBar.background", "=contrastBorder"),
        ("gitDecoration.modifiedResourceForeground", "#895503"), ("scrollbarSlider.background", "#0F4A8566"),
        ("panel.background", "=editor.background"), ("panel.border", "=contrastBorder"),
        ("terminal.background", "=panel.background"), ("terminal.foreground", "#292929"),
        ("terminal.selectionBackground", "=editor.selectionBackground"), ("terminalCursor.foreground", "=editorCursor.foreground"),
        ("terminal.ansiBlack", "#292929"), ("terminal.ansiRed", "#CD3131"), ("terminal.ansiGreen", "#136C13"),
        ("terminal.ansiYellow", "#949800"), ("terminal.ansiBlue", "#0451A5"), ("terminal.ansiMagenta", "#BC05BC"),
        ("terminal.ansiCyan", "#0598BC"), ("terminal.ansiWhite", "#555555"), ("terminal.ansiBrightBlack", "#666666"),
        ("terminal.ansiBrightRed", "#CD3131"), ("terminal.ansiBrightGreen", "#00BC00"), ("terminal.ansiBrightYellow", "#B5BA00"),
        ("terminal.ansiBrightBlue", "#0451A5"), ("terminal.ansiBrightMagenta", "#BC05BC"), ("terminal.ansiBrightCyan", "#0598BC"),
        ("terminal.ansiBrightWhite", "#A5A5A5"));

    /// <summary>The 16 terminal palette keys in ANSI order.</summary>
    public static readonly IReadOnlyList<string> TerminalAnsiKeys =
    [
        "terminal.ansiBlack", "terminal.ansiRed", "terminal.ansiGreen", "terminal.ansiYellow",
        "terminal.ansiBlue", "terminal.ansiMagenta", "terminal.ansiCyan", "terminal.ansiWhite",
        "terminal.ansiBrightBlack", "terminal.ansiBrightRed", "terminal.ansiBrightGreen", "terminal.ansiBrightYellow",
        "terminal.ansiBrightBlue", "terminal.ansiBrightMagenta", "terminal.ansiBrightCyan", "terminal.ansiBrightWhite",
    ];

    public static IReadOnlyDictionary<string, DefaultColor> For(ThemeType type) => type switch
    {
        ThemeType.Light => Light,
        ThemeType.HighContrast => HighContrast,
        ThemeType.HighContrastLight => HighContrastLight,
        _ => Dark,
    };

    private static Dictionary<string, DefaultColor> Build(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(
            e => e.Key,
            e => e.Value.StartsWith('=') ? new DefaultColor(null, e.Value[1..]) : new DefaultColor(ThemeColor.Parse(e.Value), null),
            StringComparer.OrdinalIgnoreCase);
}
