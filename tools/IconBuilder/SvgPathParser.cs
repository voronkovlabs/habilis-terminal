using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace IconBuilder;

/// <summary>
/// SVG path data → WPF geometry. Handles everything SVG allows that WPF path markup does not, such as
/// minified arc flags without separators (<c>a1 1 0 011 1</c>) and implicit repeated commands.
/// </summary>
internal sealed class SvgPathParser
{
    private readonly string _data;
    private int _position;

    private SvgPathParser(string data) => _data = data;

    public static StreamGeometry Parse(string data, FillRule fillRule)
    {
        var geometry = new StreamGeometry { FillRule = fillRule };
        using (var context = geometry.Open())
        {
            new SvgPathParser(data).Run(context);
        }

        geometry.Freeze();
        return geometry;
    }

    private void Run(StreamGeometryContext context)
    {
        var current = new Point();
        var start = new Point();
        var lastControl = new Point();
        var command = ' ';
        var previous = ' ';
        var figureOpen = false;

        while (true)
        {
            SkipSeparators();
            if (_position >= _data.Length)
            {
                break;
            }

            var c = _data[_position];
            if (char.IsLetter(c))
            {
                command = c;
                _position++;
            }
            else if (command == ' ')
            {
                throw new FormatException($"Path data must start with a command: '{_data}'.");
            }

            var relative = char.IsLower(command);
            var origin = relative ? current : new Point();
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    current = Add(origin, ReadPoint());
                    start = current;
                    context.BeginFigure(current, isFilled: true, isClosed: false);
                    figureOpen = true;

                    // Further pairs after M are implicit L.
                    command = relative ? 'l' : 'L';
                    break;
                case 'L':
                    current = Add(origin, ReadPoint());
                    context.LineTo(current, true, false);
                    break;
                case 'H':
                    current = new Point((relative ? current.X : 0) + ReadNumber(), current.Y);
                    context.LineTo(current, true, false);
                    break;
                case 'V':
                    current = new Point(current.X, (relative ? current.Y : 0) + ReadNumber());
                    context.LineTo(current, true, false);
                    break;
                case 'C':
                {
                    var c1 = Add(origin, ReadPoint());
                    var c2 = Add(origin, ReadPoint());
                    current = Add(origin, ReadPoint());
                    context.BezierTo(c1, c2, current, true, false);
                    lastControl = c2;
                    break;
                }

                case 'S':
                {
                    var c1 = previous is 'C' or 'S' ? Reflect(lastControl, current) : current;
                    var c2 = Add(origin, ReadPoint());
                    current = Add(origin, ReadPoint());
                    context.BezierTo(c1, c2, current, true, false);
                    lastControl = c2;
                    break;
                }

                case 'Q':
                {
                    var control = Add(origin, ReadPoint());
                    current = Add(origin, ReadPoint());
                    context.QuadraticBezierTo(control, current, true, false);
                    lastControl = control;
                    break;
                }

                case 'T':
                {
                    var control = previous is 'Q' or 'T' ? Reflect(lastControl, current) : current;
                    current = Add(origin, ReadPoint());
                    context.QuadraticBezierTo(control, current, true, false);
                    lastControl = control;
                    break;
                }

                case 'A':
                {
                    var rx = Math.Abs(ReadNumber());
                    var ry = Math.Abs(ReadNumber());
                    var rotation = ReadNumber();
                    var largeArc = ReadFlag();
                    var sweep = ReadFlag();
                    current = Add(origin, ReadPoint());
                    context.ArcTo(current, new Size(rx, ry), rotation, largeArc,
                        sweep ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
                    break;
                }

                case 'Z':
                    if (figureOpen)
                    {
                        context.LineTo(start, true, false);
                    }

                    current = start;
                    figureOpen = false;
                    previous = 'Z';

                    // A new figure starts at the closing point if drawing continues without M.
                    SkipSeparators();
                    if (_position < _data.Length && !char.IsLetter(_data[_position]))
                    {
                        throw new FormatException("Numbers after Z.");
                    }

                    if (_position < _data.Length && char.ToUpperInvariant(_data[_position]) != 'M')
                    {
                        context.BeginFigure(start, isFilled: true, isClosed: false);
                        figureOpen = true;
                    }

                    continue;
                default:
                    throw new FormatException($"Unsupported path command '{command}'.");
            }

            previous = char.ToUpperInvariant(command);
        }
    }

    private static Point Add(Point origin, Point value) => new(origin.X + value.X, origin.Y + value.Y);

    private static Point Reflect(Point control, Point around) => new((2 * around.X) - control.X, (2 * around.Y) - control.Y);

    private Point ReadPoint()
    {
        var x = ReadNumber();
        var y = ReadNumber();
        return new Point(x, y);
    }

    private bool ReadFlag()
    {
        SkipSeparators();
        var c = _data[_position++];
        return c switch
        {
            '0' => false,
            '1' => true,
            _ => throw new FormatException($"Invalid arc flag '{c}'."),
        };
    }

    private double ReadNumber()
    {
        SkipSeparators();
        var begin = _position;
        if (_position < _data.Length && _data[_position] is '+' or '-')
        {
            _position++;
        }

        var seenDot = false;
        var seenExponent = false;
        while (_position < _data.Length)
        {
            var c = _data[_position];
            if (char.IsAsciiDigit(c))
            {
                _position++;
            }
            else if (c == '.' && !seenDot && !seenExponent)
            {
                seenDot = true;
                _position++;
            }
            else if (c is 'e' or 'E' && !seenExponent)
            {
                seenExponent = true;
                _position++;
                if (_position < _data.Length && _data[_position] is '+' or '-')
                {
                    _position++;
                }
            }
            else
            {
                break;
            }
        }

        if (begin == _position)
        {
            throw new FormatException($"Number expected at {begin} in '{_data}'.");
        }

        return double.Parse(_data.AsSpan(begin, _position - begin), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private void SkipSeparators()
    {
        while (_position < _data.Length && (char.IsWhiteSpace(_data[_position]) || _data[_position] == ','))
        {
            _position++;
        }
    }
}
