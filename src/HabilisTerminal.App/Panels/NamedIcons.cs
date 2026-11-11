using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Media;
using HabilisTerminal.App.Theming;

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

        // Code hosting (issue #222), from codicon glyphs: people, repositories, branches.
        ["user"] = Glyphs(("#F0883E", Person, 1.0, 0, 0)),
        ["organization"] = Glyphs(("#A371F7", OrganizationGlyph, 1.0, 0, 0)),
        ["repository"] = Glyphs(("#D4A72C", Repo, 1.0, 0, 0)),
        ["repository-private"] = Glyphs(("#D4A72C", Repo, 1.0, -1.5, 0), ("#E5534B", Lock, 0.6, 7, 6.5)),
        ["repository-archived"] = Glyphs(("#8B949E", Archive, 1.0, 0, 0)),
        ["branch"] = Glyphs(("#3FB950", GitBranch, 1.0, 0, 0)),
        ["branch-default"] = Glyphs(("#E3B341", GitBranch, 1.0, 0, 0)),
    };

    private const string Repo = "\uEA62";
    private const string Person = "\uEA67";
    private const string GitBranch = "\uEA68";
    private const string Lock = "\uEA75";
    private const string OrganizationGlyph = "\uEA7E";
    private const string Archive = "\uEA98";

    public static Drawing? Get(string name) => Drawings.TryGetValue(name, out var drawing) ? drawing : null;

    /// <summary>
    /// Codicon glyphs, each fitted into the 16 x 16 cell at <paramref name="parts"/>' scale and offset, in one colour each.
    /// A glyph missing from the font draws nothing.
    /// </summary>
    private static DrawingGroup Glyphs(params (string Color, string Glyph, double Scale, double X, double Y)[] parts)
    {
        var group = new DrawingGroup();
        foreach (var (color, glyph, scale, x, y) in parts)
        {
            if (Codicon.GeometryOf(glyph) is not { } outline || outline.Bounds.IsEmpty)
            {
                continue;
            }

            var bounds = outline.Bounds;
            var fit = 16 * scale / Math.Max(bounds.Width, bounds.Height);
            var transform = new TransformGroup();
            transform.Children.Add(new TranslateTransform(-bounds.X, -bounds.Y));
            transform.Children.Add(new ScaleTransform(fit, fit));
            transform.Children.Add(new TranslateTransform(x + (16 * scale - bounds.Width * fit) / 2, y + (16 * scale - bounds.Height * fit) / 2));
            var shape = outline.Clone();
            shape.Transform = transform;
            var brush = new SolidColorBrush((Color)Color.Parse(color));
            group.Children.Add(new GeometryDrawing { Brush = brush, Geometry = shape });
        }
        return group;
    }

    private static DrawingGroup Group(params (string Color, string Data)[] shapes)
    {
        var group = new DrawingGroup();
        foreach (var (color, data) in shapes)
        {
            var brush = new SolidColorBrush((Color)Color.Parse(color));
            group.Children.Add(new GeometryDrawing { Brush = brush, Geometry = Geometry.Parse(data) });
        }
        return group;
    }
}
