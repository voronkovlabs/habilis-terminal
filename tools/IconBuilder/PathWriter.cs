using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace IconBuilder;

/// <summary>Writes WPF path markup with coordinates rounded to 3 decimals (WPF's own ToString keeps full doubles).</summary>
internal static class PathWriter
{
    public static string Write(Geometry geometry)
    {
        var path = PathGeometry.CreateFromGeometry(geometry);
        var text = new StringBuilder(path.FillRule == FillRule.EvenOdd ? "F0" : "F1");
        foreach (var figure in path.Figures)
        {
            text.Append('M');
            Point(text, figure.StartPoint);
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        text.Append('L');
                        Point(text, line.Point);
                        break;
                    case PolyLineSegment lines:
                        text.Append('L');
                        Points(text, lines.Points);
                        break;
                    case BezierSegment bezier:
                        text.Append('C');
                        Points(text, new PointCollection { bezier.Point1, bezier.Point2, bezier.Point3 });
                        break;
                    case PolyBezierSegment beziers:
                        text.Append('C');
                        Points(text, beziers.Points);
                        break;
                    case QuadraticBezierSegment quadratic:
                        text.Append('Q');
                        Points(text, new PointCollection { quadratic.Point1, quadratic.Point2 });
                        break;
                    case PolyQuadraticBezierSegment quadratics:
                        text.Append('Q');
                        Points(text, quadratics.Points);
                        break;
                    case ArcSegment arc:
                        text.Append('A');
                        Number(text, arc.Size.Width).Append(',');
                        Number(text, arc.Size.Height).Append(' ');
                        Number(text, arc.RotationAngle).Append(' ');
                        text.Append(arc.IsLargeArc ? '1' : '0').Append(' ');
                        text.Append(arc.SweepDirection == SweepDirection.Clockwise ? '1' : '0').Append(' ');
                        Point(text, arc.Point);
                        break;
                }
            }

            if (figure.IsClosed)
            {
                text.Append('Z');
            }
        }

        return text.ToString();
    }

    private static void Points(StringBuilder text, PointCollection points)
    {
        for (var i = 0; i < points.Count; i++)
        {
            if (i > 0)
            {
                text.Append(' ');
            }

            Point(text, points[i]);
        }
    }

    private static void Point(StringBuilder text, Point point)
    {
        Number(text, point.X).Append(',');
        Number(text, point.Y);
    }

    private static StringBuilder Number(StringBuilder text, double value) =>
        text.Append(Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture));
}
