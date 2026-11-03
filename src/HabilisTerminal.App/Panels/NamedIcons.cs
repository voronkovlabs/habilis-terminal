using System.Windows.Media;

namespace HabilisTerminal.App.Panels;

/// <summary>
/// The host's own 16 × 16 icons that plugin rows name through <c>PanelEntry.Icon</c> (<c>PanelIcons</c>, issue #69).
/// Fixed colours, like the file icon themes.
/// </summary>
internal static class NamedIcons
{
    private static readonly Dictionary<string, Drawing> Drawings = new(StringComparer.OrdinalIgnoreCase)
    {
        // A pail, as AWS draws S3 buckets: green body, lighter rim.
        ["bucket"] = Group(
            ("#3F8624", "M1.6,3.6 L3.6,13.4 A4.4,1.5 0 0 0 12.4,13.4 L14.4,3.6 Z"),
            ("#7AA116", "M1.6,3.6 A6.4,1.9 0 1 0 14.4,3.6 A6.4,1.9 0 1 0 1.6,3.6 Z"),
            ("#2A5C18", "M3.2,4.6 A4.8,1.2 0 0 0 12.8,4.6 A4.8,1.3 0 0 1 3.2,4.6 Z")),

        // A graphics card: board with a fan and contacts.
        ["gpu"] = Group(
            ("#76B900", "M1,4 H15 V12 H1 Z"),
            ("#1E1E1E", "M10.5,8 A2.6,2.6 0 1 0 5.3,8 A2.6,2.6 0 1 0 10.5,8 Z"),
            ("#76B900", "M9,8 A1.1,1.1 0 1 0 6.8,8 A1.1,1.1 0 1 0 9,8 Z"),
            ("#C9A227", "M3,12 H13 V14 H3 Z")),

        // A running program: a window with a prompt.
        ["process"] = Group(
            ("#5A9BD5", "M1.5,2.5 H14.5 V13.5 H1.5 Z"),
            ("#1E1E1E", "M2.5,5 H13.5 V12.5 H2.5 Z"),
            ("#E0E0E0", "M4,6.6 L6.6,8.4 L4,10.2 L4,9.2 L5.2,8.4 L4,7.6 Z M7.2,10 H10.5 V10.9 H7.2 Z")),
    };

    public static Drawing? Get(string name) => Drawings.TryGetValue(name, out var drawing) ? drawing : null;

    private static DrawingGroup Group(params (string Color, string Data)[] shapes)
    {
        var group = new DrawingGroup();
        foreach (var (color, data) in shapes)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            group.Children.Add(new GeometryDrawing(brush, null, Geometry.Parse(data)));
        }

        group.Freeze();
        return group;
    }
}
