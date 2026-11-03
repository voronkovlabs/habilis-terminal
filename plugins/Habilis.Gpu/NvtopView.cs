using System.Globalization;
using System.Windows;
using System.Windows.Media;
using HabilisTerminal.Plugins.Wpf;

namespace Habilis.Gpu;

/// <summary>
/// The GPU monitor drawn like nvtop (issue #79): per GPU a header and bars for load and memory, CPU and RAM bars, a
/// chart of the recent values, and the processes using the GPU with their GPU load, GPU memory, CPU load and RAM.
/// Drawn in one pass in the host's colours; never takes the keyboard.
/// </summary>
internal sealed class NvtopView : FrameworkElement
{
    private const double Gap = 8;
    private static readonly Typeface Mono = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface MonoBold = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private LocalSample? _sample;
    private IReadOnlyDictionary<string, (IReadOnlyList<double?> Load, IReadOnlyList<double?> Memory)> _gpuHistory = new Dictionary<string, (IReadOnlyList<double?>, IReadOnlyList<double?>)>();
    private IReadOnlyList<double?> _cpuHistory = [];
    private IReadOnlyList<double?> _ramHistory = [];
    private bool _paused;
    private readonly List<(Rect Cell, string Text)> _commands = [];
    private readonly System.Windows.Controls.ToolTip _tip = new() { MaxWidth = 900, Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
    private readonly System.Windows.Threading.DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private string? _pending;

    public NvtopView()
    {
        Focusable = false;
        ClipToBounds = true;

        // Hovering a process's COMMAND cell shows its full command line (issue #80). The tooltip is opened by hand:
        // the cells change with every sample, which WPF's own tooltip handling does not follow.
        _tip.PlacementTarget = this;
        _hover.Tick += (_, _) =>
        {
            _hover.Stop();
            if (_pending is { } text)
            {
                _tip.Content = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
                _tip.IsOpen = true;
            }
        };
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var text = TipAt(e.GetPosition(this));
        if (text == _pending)
        {
            return;
        }

        // Another cell or none: the open tooltip closes and the new one waits for the hover delay.
        _pending = text;
        _tip.IsOpen = false;
        _hover.Stop();
        if (text is not null)
        {
            _hover.Start();
        }
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _pending = null;
        _hover.Stop();
        _tip.IsOpen = false;
    }

    public double FontSize { get; set; } = 13;

    public void Show(
        LocalSample sample,
        IReadOnlyDictionary<string, (IReadOnlyList<double?> Load, IReadOnlyList<double?> Memory)> gpuHistory,
        IReadOnlyList<double?> cpuHistory,
        IReadOnlyList<double?> ramHistory,
        bool paused)
    {
        _sample = sample;
        _gpuHistory = gpuHistory;
        _cpuHistory = cpuHistory;
        _ramHistory = ramHistory;
        _paused = paused;
        InvalidateVisual();
    }

    private string? TipAt(Point point) => _commands.FirstOrDefault(c => c.Cell.Contains(point)).Text;

    protected override void OnRender(DrawingContext dc)
    {
        _commands.Clear();
        var background = Brush(HostBrushes.Background, Brushes.Black);
        dc.DrawRectangle(background, null, new Rect(RenderSize));
        if (_sample is not { } sample)
        {
            Text(dc, "Sampling…", Gap, Gap, Brush(HostBrushes.Muted, Brushes.Gray));
            return;
        }

        var line = Math.Ceiling(FontSize * 1.45);
        var width = Math.Max(0, ActualWidth - (2 * Gap));
        var y = Gap;
        var foreground = Brush(HostBrushes.Foreground, Brushes.White);
        var muted = Brush(HostBrushes.Muted, Brushes.Gray);
        var colours = new[] { Brush(HostBrushes.Highlight, Brushes.DeepSkyBlue), Brush(HostBrushes.Good, Brushes.LimeGreen), Brush(HostBrushes.Warning, Brushes.Gold), Brush(HostBrushes.Bad, Brushes.OrangeRed) };

        foreach (var gpu in sample.Gpus.Gpus)
        {
            Text(dc, $"GPU{gpu.Index} {gpu.Name}", Gap, y, foreground, bold: true);
            var details = $"TEMP {TextChart.Value(gpu.TemperatureC, "°C")}  POW {TextChart.Value(gpu.PowerW, "", "0")}/{TextChart.Value(gpu.PowerLimitW, " W")}  FAN {TextChart.Value(gpu.FanPercent, "%")}";
            TextRight(dc, details, Gap + width, y, muted);
            y += line;
            var half = (width - Gap) / 2;
            Bar(dc, "GPU", gpu.Utilization, TextChart.Value(gpu.Utilization, "%"), Gap, y, half, line);
            Bar(dc, "MEM", gpu.MemoryPercent, $"{Gib(gpu.MemoryUsedMiB)}/{Gib(gpu.MemoryTotalMiB)} GiB", Gap + half + Gap, y, half, line);
            y += line + 4;
        }

        var halfWidth = (width - Gap) / 2;
        Bar(dc, "CPU", sample.System.CpuPercent, TextChart.Value(sample.System.CpuPercent, "%"), Gap, y, halfWidth, line);
        Bar(dc, "RAM", sample.System.RamPercent, $"{Gib(sample.System.RamUsedMiB)}/{Gib(sample.System.RamTotalMiB)} GiB", Gap + halfWidth + Gap, y, halfWidth, line);
        y += line + Gap;

        // Separate charts in a grid of two columns (GPU load and memory per GPU, CPU, RAM); they take about half of
        // what is left, the processes the rest.
        var charts = new List<(string Title, IReadOnlyList<double?> Values, double? Now, Brush Colour)>();
        foreach (var gpu in sample.Gpus.Gpus)
        {
            var history = _gpuHistory.TryGetValue(gpu.Uuid, out var h) ? h : ([], []);
            charts.Add(($"GPU{gpu.Index} load", history.Load, gpu.Utilization, colours[0]));
            charts.Add(($"GPU{gpu.Index} memory", history.Memory, gpu.MemoryPercent, colours[1]));
        }

        charts.Add(("CPU", _cpuHistory, sample.System.CpuPercent, colours[2]));
        charts.Add(("RAM", _ramHistory, sample.System.RamPercent, colours[3]));
        var rows = (charts.Count + 1) / 2;
        var gridHeight = Math.Max(rows * line * 3, (ActualHeight - y) * 0.5);
        var cellWidth = (width - Gap) / 2;
        var cellHeight = (gridHeight - ((rows - 1) * Gap)) / rows;
        for (var i = 0; i < charts.Count; i++)
        {
            var (title, values, now, colour) = charts[i];
            var cell = new Rect(Gap + ((i % 2) * (cellWidth + Gap)), y + ((i / 2) * (cellHeight + Gap)), cellWidth, cellHeight);
            Chart(dc, title, values, now, colour, cell, line, muted);
        }

        y += gridHeight + Gap;

        Processes(dc, sample, y, width, line, foreground, muted);
    }

    private static string Gib(double? mib) => mib is { } v ? (v / 1024).ToString("0.0", CultureInfo.InvariantCulture) : "n/a";

    /// <summary>One chart: its title and current value above a framed 0–100 plot with grid lines at 25, 50 and 75.</summary>
    private void Chart(DrawingContext dc, string title, IReadOnlyList<double?> values, double? now, Brush colour, Rect cell, double line, Brush muted)
    {
        Text(dc, title, cell.Left, cell.Top, muted);
        TextRight(dc, TextChart.Value(now, "%"), cell.Right, cell.Top, colour, bold: true);
        var area = new Rect(cell.Left, cell.Top + line, cell.Width, Math.Max(0, cell.Height - line));
        dc.DrawRectangle(null, new Pen(Brush(HostBrushes.Border, Brushes.DimGray), 1), area);
        var grid = new Pen(Brush(HostBrushes.ProgressTrack, Brushes.DimGray), 0.5) { DashStyle = DashStyles.Dot };
        foreach (var level in new[] { 25, 50, 75 })
        {
            var y = area.Bottom - (area.Height * level / 100);
            dc.DrawLine(grid, new Point(area.Left, y), new Point(area.Right, y));
        }

        Line(dc, values, area, colour);
    }

    /// <summary>A series of 0–100 values, newest at the right edge, one step per sample.</summary>
    private static void Line(DrawingContext dc, IReadOnlyList<double?> values, Rect area, Brush colour)
    {
        if (values.Count < 2)
        {
            return;
        }

        var step = area.Width / Math.Max(1, values.Count - 1);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var open = false;
            for (var i = 0; i < values.Count; i++)
            {
                if (values[i] is not { } value)
                {
                    open = false;
                    continue;
                }

                var point = new Point(area.Left + (i * step), area.Bottom - (area.Height * Math.Clamp(value, 0, 100) / 100));
                if (!open)
                {
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                    open = true;
                }
                else
                {
                    context.LineTo(point, isStroked: true, isSmoothJoin: true);
                }
            }
        }

        geometry.Freeze();
        dc.PushClip(new RectangleGeometry(area));
        dc.DrawGeometry(null, new Pen(colour, 1.6), geometry);
        dc.Pop();
    }

    private void Processes(DrawingContext dc, LocalSample sample, double y, double width, double line, Brush foreground, Brush muted)
    {
        var header = new Rect(Gap, y, width, line);
        dc.DrawRectangle(Brush(HostBrushes.WidgetBackground, Brushes.DimGray), null, header);
        var columns = Columns();
        // Paused, the header says so in the command column (the panel's status line says it too).
        Row(dc, columns, ["PID", "TYPE", "GPU%", "GPU MEM", "CPU%", "RAM", _paused ? "COMMAND · PAUSED" : "COMMAND"], y, _paused ? Brush(HostBrushes.Warning, Brushes.Gold) : foreground, bold: true);

        y += line;
        var shown = 0;
        foreach (var process in sample.Processes)
        {
            if (y + line > ActualHeight - Gap)
            {
                Text(dc, $"+{sample.Processes.Count - shown} more", Gap, y, muted);
                return;
            }

            var tone = process.GpuPercent >= 50 ? Brush(HostBrushes.Warning, Brushes.Gold) : foreground;
            _commands.Add((new Rect(columns[^1], y, Math.Max(0, Gap + width - columns[^1]), line), process.CommandLine ?? process.Name));
            Row(dc, columns,
            [
                process.Pid.ToString(CultureInfo.InvariantCulture),
                process.Kind,
                TextChart.Value(process.GpuPercent, "%"),
                TextChart.Value(process.GpuMemoryMiB, " MiB"),
                TextChart.Value(process.CpuPercent, "%"),
                TextChart.Value(process.RamMiB, " MiB"),
                process.Name,
            ], y, tone);
            y += line;
            shown++;
        }

        if (shown == 0)
        {
            Text(dc, "No process uses the GPU.", Gap, y, muted);
        }
    }

    /// <summary>Column starts: right-aligned numbers, the command takes the rest.</summary>
    private double[] Columns()
    {
        var cell = CharWidth();
        double[] widths = [8, 9, 7, 11, 7, 11];
        var x = Gap;
        var starts = new double[widths.Length + 1];
        for (var i = 0; i < widths.Length; i++)
        {
            starts[i] = x;
            x += widths[i] * cell;
        }

        starts[^1] = x;
        return starts;
    }

    private void Row(DrawingContext dc, double[] columns, string[] cells, double y, Brush brush, bool bold = false)
    {
        var cell = CharWidth();
        for (var i = 0; i < cells.Length; i++)
        {
            if (i is 0 or 1 || i == cells.Length - 1)
            {
                Text(dc, cells[i], columns[i], y, brush, bold);
            }
            else
            {
                TextRight(dc, cells[i], columns[i + 1] - cell, y, brush, bold);
            }
        }
    }

    private void Bar(DrawingContext dc, string label, double? percent, string value, double x, double y, double width, double height)
    {
        var labelWidth = Text(dc, label, x, y, Brush(HostBrushes.Highlight, Brushes.DeepSkyBlue), bold: true) + 6;
        var track = new Rect(x + labelWidth, y + 2, Math.Max(0, width - labelWidth), height - 4);
        dc.DrawRectangle(Brush(HostBrushes.ProgressTrack, Brushes.DimGray), null, track);
        if (percent is { } p)
        {
            var fill = p >= 90 ? Brush(HostBrushes.Bad, Brushes.OrangeRed) : p >= 60 ? Brush(HostBrushes.Warning, Brushes.Gold) : Brush(HostBrushes.Good, Brushes.LimeGreen);
            dc.DrawRectangle(fill, null, new Rect(track.X, track.Y, track.Width * Math.Clamp(p, 0, 100) / 100, track.Height));
        }

        TextRight(dc, value, track.Right - 4, y, Brush(HostBrushes.Foreground, Brushes.White), bold: true);
    }

    private double CharWidth() => Formatted("0", Brushes.White, false, false).WidthIncludingTrailingWhitespace;

    private double Text(DrawingContext dc, string text, double x, double y, Brush brush, bool bold = false, bool small = false)
    {
        var formatted = Formatted(text, brush, bold, small);
        dc.DrawText(formatted, new Point(x, y));
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private void TextRight(DrawingContext dc, string text, double right, double y, Brush brush, bool bold = false)
    {
        var formatted = Formatted(text, brush, bold, false);
        dc.DrawText(formatted, new Point(right - formatted.WidthIncludingTrailingWhitespace, y));
    }

    private FormattedText Formatted(string text, Brush brush, bool bold, bool small) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? MonoBold : Mono, small ? FontSize * 0.8 : FontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private Brush Brush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
}
