using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace IconBuilder;

/// <summary>One filled shape of an icon.</summary>
internal sealed record IconPath(Geometry Geometry, Color Fill);

/// <summary>
/// The subset of SVG used by icon sets: path, circle, ellipse, rect, polygon, groups with fill / opacity /
/// transform, <c>use</c> references and gradients (approximated by their first stop colour). Strokes and clip paths
/// are ignored and counted as warnings.
/// </summary>
internal sealed partial class SvgIconConverter
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    private readonly Dictionary<string, XElement> _byId = [];
    private readonly List<IconPath> _paths = [];

    public int Warnings { get; private set; }

    public (Rect ViewBox, IReadOnlyList<IconPath> Paths) Convert(string svgText)
    {
        _byId.Clear();
        _paths.Clear();
        var root = XDocument.Parse(svgText).Root ?? throw new FormatException("Empty SVG.");
        foreach (var element in root.Descendants())
        {
            if (element.Attribute("id")?.Value is { } id)
            {
                _byId[id] = element;
            }
        }

        var viewBox = ParseViewBox(root);
        Walk(root, new Style(Colors.Black, 1, Transform.Identity));
        return (viewBox, _paths.ToArray());
    }

    private static Rect ParseViewBox(XElement root)
    {
        if (root.Attribute("viewBox")?.Value is { } value)
        {
            var parts = Numbers(value);
            if (parts.Length == 4)
            {
                return new Rect(parts[0], parts[1], parts[2], parts[3]);
            }
        }

        var width = Number(root.Attribute("width")?.Value) ?? 16;
        var height = Number(root.Attribute("height")?.Value) ?? 16;
        return new Rect(0, 0, width, height);
    }

    private void Walk(XElement element, Style inherited)
    {
        var name = element.Name.LocalName;
        if (name is "defs" or "linearGradient" or "radialGradient" or "clipPath" or "mask" or "title" or "desc" or "style" or "metadata")
        {
            if (name is "clipPath" or "mask")
            {
                Warnings++;
            }

            return;
        }

        var style = Apply(element, inherited);
        switch (name)
        {
            case "svg" or "g":
                foreach (var child in element.Elements())
                {
                    Walk(child, style);
                }

                return;
            case "use":
                var href = (element.Attribute(XLink + "href") ?? element.Attribute("href"))?.Value;
                if (href is { Length: > 1 } && _byId.TryGetValue(href[1..], out var target))
                {
                    var offset = new TranslateTransform(Number(element.Attribute("x")?.Value) ?? 0, Number(element.Attribute("y")?.Value) ?? 0);
                    Walk(target, style with { Transform = Combine(offset, style.Transform) });
                }

                return;
        }

        var geometry = Shape(element);
        if (geometry is null || style.Fill is null)
        {
            return;
        }

        if (!style.Transform.Value.IsIdentity)
        {
            geometry = Geometry.Combine(geometry, Geometry.Empty, GeometryCombineMode.Union, style.Transform);
        }

        var color = style.Fill.Value;
        color.A = (byte)Math.Round(color.A * Math.Clamp(style.Opacity, 0, 1));
        _paths.Add(new IconPath(geometry, color));
    }

    private Geometry? Shape(XElement element)
    {
        double Get(string attribute) => Number(element.Attribute(attribute)?.Value) ?? 0;
        var fillRule = (Attr(element, "fill-rule") ?? Attr(element, "clip-rule")) == "evenodd" ? FillRule.EvenOdd : FillRule.Nonzero;
        switch (element.Name.LocalName)
        {
            case "path":
                var data = element.Attribute("d")?.Value;
                return string.IsNullOrWhiteSpace(data) ? null : SvgPathParser.Parse(data, fillRule);
            case "circle":
                return new EllipseGeometry(new Point(Get("cx"), Get("cy")), Get("r"), Get("r"));
            case "ellipse":
                return new EllipseGeometry(new Point(Get("cx"), Get("cy")), Get("rx"), Get("ry"));
            case "rect":
                var rx = Number(element.Attribute("rx")?.Value) ?? Number(element.Attribute("ry")?.Value) ?? 0;
                var ry = Number(element.Attribute("ry")?.Value) ?? rx;
                return new RectangleGeometry(new Rect(Get("x"), Get("y"), Get("width"), Get("height")), rx, ry);
            case "polygon" or "polyline":
                var points = Numbers(element.Attribute("points")?.Value ?? string.Empty);
                if (points.Length < 4)
                {
                    return null;
                }

                var figure = new PathFigure { StartPoint = new Point(points[0], points[1]), IsClosed = true, IsFilled = true };
                for (var i = 2; i + 1 < points.Length; i += 2)
                {
                    figure.Segments.Add(new LineSegment(new Point(points[i], points[i + 1]), true));
                }

                return new PathGeometry([figure]) { FillRule = fillRule };
            default:
                Warnings++;
                return null;
        }
    }

    private Style Apply(XElement element, Style inherited)
    {
        var style = inherited;
        var fill = Attr(element, "fill");
        if (fill is not null)
        {
            style = style with { Fill = ParsePaint(fill) };
        }

        if (Number(Attr(element, "opacity")) is { } opacity)
        {
            style = style with { Opacity = style.Opacity * opacity };
        }

        if (Number(Attr(element, "fill-opacity")) is { } fillOpacity)
        {
            style = style with { Opacity = style.Opacity * fillOpacity };
        }

        if (element.Attribute("transform")?.Value is { } transform)
        {
            style = style with { Transform = Combine(ParseTransform(transform), inherited.Transform) };
        }

        if (Attr(element, "stroke") is { } stroke && stroke != "none")
        {
            Warnings++;
        }

        return style;
    }

    private Color? ParsePaint(string value)
    {
        value = value.Trim();
        if (value == "none")
        {
            return null;
        }

        if (value.StartsWith("url(#", StringComparison.Ordinal))
        {
            var id = value[5..value.IndexOf(')', StringComparison.Ordinal)];
            if (_byId.TryGetValue(id, out var gradient))
            {
                var stop = gradient.Descendants(Svg + "stop").FirstOrDefault() ?? gradient.Descendants().FirstOrDefault(e => e.Name.LocalName == "stop");
                if (stop is not null && Attr(stop, "stop-color") is { } stopColor)
                {
                    return ParsePaint(stopColor);
                }

                // xlink:href to another gradient that holds the stops.
                var href = (gradient.Attribute(XLink + "href") ?? gradient.Attribute("href"))?.Value;
                if (href is { Length: > 1 })
                {
                    return ParsePaint($"url({href})");
                }
            }

            return Colors.Gray;
        }

        if (value == "currentColor")
        {
            return Colors.Gray;
        }

        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch (FormatException)
        {
            Warnings++;
            return Colors.Gray;
        }
    }

    /// <summary>An attribute or the same property inside <c>style="…"</c>.</summary>
    private static string? Attr(XElement element, string name)
    {
        if (element.Attribute("style")?.Value is { } style)
        {
            foreach (var declaration in style.Split(';'))
            {
                var colon = declaration.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && declaration[..colon].Trim() == name)
                {
                    return declaration[(colon + 1)..].Trim();
                }
            }
        }

        return element.Attribute(name)?.Value;
    }

    private static TransformGroup ParseTransform(string value)
    {
        var group = new TransformGroup();
        foreach (Match match in TransformPattern().Matches(value))
        {
            var args = Numbers(match.Groups[2].Value);
            Transform? transform = match.Groups[1].Value switch
            {
                "translate" => new TranslateTransform(args.ElementAtOrDefault(0), args.ElementAtOrDefault(1)),
                "scale" => new ScaleTransform(args[0], args.Length > 1 ? args[1] : args[0]),
                "rotate" => args.Length >= 3 ? new RotateTransform(args[0], args[1], args[2]) : new RotateTransform(args[0]),
                "matrix" when args.Length == 6 => new MatrixTransform(args[0], args[1], args[2], args[3], args[4], args[5]),
                _ => null,
            };
            if (transform is not null)
            {
                group.Children.Add(transform);
            }
        }

        // SVG applies the listed transforms right to left; WPF groups apply first to last.
        var reversed = new TransformGroup();
        for (var i = group.Children.Count - 1; i >= 0; i--)
        {
            reversed.Children.Add(group.Children[i]);
        }

        return reversed;
    }

    private static TransformGroup Combine(Transform inner, Transform outer)
    {
        var group = new TransformGroup();
        group.Children.Add(inner);
        group.Children.Add(outer);
        return group;
    }

    private static double? Number(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim().TrimEnd('%').Replace("px", string.Empty, StringComparison.Ordinal);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static double[] Numbers(string value) =>
        NumberPattern().Matches(value).Select(m => double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();

    [GeneratedRegex(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"(translate|scale|rotate|matrix|skewX|skewY)\s*\(([^)]*)\)")]
    private static partial Regex TransformPattern();

    private sealed record Style(Color? Fill, double Opacity, Transform Transform);
}
