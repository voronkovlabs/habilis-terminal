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

    public NvtopView()
    {
        Focusable = false;
        ClipToBounds = true;
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

    protected override void OnRender(DrawingContext dc)
    {
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

        // The chart takes about 40% of what is left; the processes get the rest.
        var chartHeight = Math.Max(line * 4, (ActualHeight - y) * 0.4);
        y = Legend(dc, sample, y, line, colours, muted);
        Chart(dc, sample, new Rect(Gap, y, width, chartHeight), colours, muted);
        y += chartHeight + Gap;

        Processes(dc, sample, y, width, line, foreground, muted);
        if (_paused)
        {
            TextRight(dc, "paused (Shift+F4)", Gap + width, Gap, Brush(HostBrushes.Warning, Brushes.Gold), bold: true);
        }
    }

    private static string Gib(double? mib) => mib is { } v ? (v / 1024).ToString("0.0", CultureInfo.InvariantCulture) : "n/a";

    private double Legend(DrawingContext dc, LocalSample sample, double y, double line, Brush[] colours, Brush muted)
    {
        var x = Gap;
        var index = 0;
        foreach (var gpu in sample.Gpus.Gpus)
        {
            x = Key(dc, $"GPU{gpu.Index} %", colours[index++ % colours.Length], x, y, muted);
            x = Key(dc, $"GPU{gpu.Index} mem", colours[index++ % colours.Length], x, y, muted);
        }

        x = Key(dc, "CPU", colours[index++ % colours.Length], x, y, muted);
        Key(dc, "RAM", colours[index % colours.Length], x, y, muted);
        return y + line;
    }

    private double Key(DrawingContext dc, string label, Brush colour, double x, double y, Brush muted)
    {
        var mid = y + (FontSize * 0.7);
        dc.DrawLine(new Pen(colour, 2), new Point(x, mid), new Point(x + 16, mid));
        return x + 22 + Text(dc, label, x + 20, y, muted) + 12;
    }

    private void Chart(DrawingContext dc, LocalSample sample, Rect area, Brush[] colours, Brush muted)
    {
        var border = new Pen(Brush(HostBrushes.Border, Brushes.DimGray), 1);
        dc.DrawRectangle(null, border, area);
        var grid = new Pen(Brush(HostBrushes.ProgressTrack, Brushes.DimGray), 0.5) { DashStyle = DashStyles.Dot };
        foreach (var level in new[] { 25, 50, 75 })
        {
            var y = area.Bottom - (area.Height * level / 100);
            dc.DrawLine(grid, new Point(area.Left, y), new Point(area.Right, y));
            Text(dc, level.ToString(CultureInfo.InvariantCulture), area.Left + 2, y - FontSize, muted, small: true);
        }

        var index = 0;
        foreach (var gpu in sample.Gpus.Gpus)
        {
            if (_gpuHistory.TryGetValue(gpu.Uuid, out var history))
            {
                Line(dc, history.Load, area, colours[index % colours.Length]);
                Line(dc, history.Memory, area, colours[(index + 1) % colours.Length]);
            }

            index += 2;
        }

        Line(dc, _cpuHistory, area, colours[index++ % colours.Length]);
        Line(dc, _ramHistory, area, colours[index % colours.Length]);
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
        Row(dc, columns, ["PID", "TYPE", "GPU%", "GPU MEM", "CPU%", "RAM", "COMMAND"], y, foreground, bold: true);
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
