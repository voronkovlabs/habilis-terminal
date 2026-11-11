using Avalonia.Media;

namespace HabilisTerminal.App.Theming;

/// <summary>Codicons (VS Code UI icons, CC BY 4.0) used by the UI, as characters of the bundled codicon font.</summary>
internal static class Codicon
{
    private static readonly Typeface Font = new(new FontFamily("avares://habi/Assets#codicon"));
    private static readonly Dictionary<string, Geometry?> Geometries = [];

    /// <summary>The glyph of a codicon character as geometry (no font lookup involved), or null.</summary>
    public static Geometry? GeometryOf(string? icon)
    {
        if (string.IsNullOrEmpty(icon))
        {
            return null;
        }

        if (!Geometries.TryGetValue(icon, out var geometry))
        {
            geometry = new FormattedText(icon[..1], System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 16, null)
                .BuildGeometry(default);
            Geometries[icon] = geometry;
        }

        return geometry;
    }

    public const string Folder = "";
    public const string Home = "";
    public const string Desktop = "";
    public const string Download = "";
    public const string Document = "";
    public const string History = "";
    public const string Drive = "";
    public const string NetworkDrive = "";
    public const string RemovableDrive = "";
    public const string Disc = "";
    public const string Check = "";
    public const string ArrowUp = "";
    public const string Feedback = "";
    public const string ArrowDown = "";
}
