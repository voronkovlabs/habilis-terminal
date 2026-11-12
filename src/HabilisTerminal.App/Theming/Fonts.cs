namespace HabilisTerminal.App.Theming;

/// <summary>Font lists with fallbacks for each system (#259): what Windows has first, then macOS and Linux.</summary>
internal static class Fonts
{
    /// <summary>Monospaced: without a fallback, Avalonia draws text in the system's proportional font and the console's grid breaks.</summary>
    public const string Mono = "Cascadia Mono, Consolas, Menlo, SF Mono, DejaVu Sans Mono, Liberation Mono";

    /// <summary>A configured font, then the monospaced fallbacks.</summary>
    public static string MonoWith(string? family) => string.IsNullOrWhiteSpace(family) ? Mono : family + ", " + Mono;
}
