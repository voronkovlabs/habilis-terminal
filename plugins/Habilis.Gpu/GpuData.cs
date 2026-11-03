using System.Globalization;
using System.Text;

namespace Habilis.Gpu;

/// <summary>One GPU at one moment; a value the driver does not report ("[N/A]") is null.</summary>
internal sealed record GpuSample(
    int Index,
    string Uuid,
    string Name,
    double? Utilization,
    double? MemoryUtilization,
    double? MemoryUsedMiB,
    double? MemoryTotalMiB,
    double? TemperatureC,
    double? PowerW,
    double? PowerLimitW,
    double? FanPercent)
{
    /// <summary>Used memory as a share of the total, 0–100.</summary>
    public double? MemoryPercent => MemoryUsedMiB is { } used && MemoryTotalMiB is > 0 ? used * 100 / MemoryTotalMiB : null;
}

/// <summary>A process using a GPU (compute or graphics).</summary>
internal sealed record GpuProcess(string GpuUuid, int Pid, string Name, double? MemoryMiB);

/// <summary>All GPUs and their processes at one moment.</summary>
internal sealed record GpuSnapshot(IReadOnlyList<GpuSample> Gpus, IReadOnlyList<GpuProcess> Processes, DateTime TakenUtc);

/// <summary>
/// nvidia-smi's CSV output (<c>--format=csv,noheader,nounits</c>): the queries the plugin runs and their parsing.
/// </summary>
internal static class NvidiaSmiFormat
{
    public const string GpuQuery = "--query-gpu=index,uuid,name,utilization.gpu,utilization.memory,memory.used,memory.total,temperature.gpu,power.draw,power.limit,fan.speed --format=csv,noheader,nounits";

    public const string ComputeQuery = "--query-compute-apps=gpu_uuid,pid,process_name,used_memory --format=csv,noheader,nounits";

    public static IReadOnlyList<GpuSample> ParseGpus(string csv)
    {
        var gpus = new List<GpuSample>();
        foreach (var fields in Rows(csv))
        {
            if (fields.Length < 11 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                continue;
            }

            gpus.Add(new GpuSample(index, fields[1], fields[2], Number(fields[3]), Number(fields[4]), Number(fields[5]), Number(fields[6]), Number(fields[7]), Number(fields[8]), Number(fields[9]), Number(fields[10])));
        }

        return gpus;
    }

    public static IReadOnlyList<GpuProcess> ParseProcesses(string csv)
    {
        var processes = new List<GpuProcess>();
        foreach (var fields in Rows(csv))
        {
            if (fields.Length >= 4 && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                // A process name may contain commas: everything between the pid and the last field.
                var name = string.Join(", ", fields[2..^1]);
                processes.Add(new GpuProcess(fields[0], pid, name, Number(fields[^1])));
            }
        }

        return processes;
    }

    private static IEnumerable<string[]> Rows(string csv) =>
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("No running", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Split(',', StringSplitOptions.TrimEntries));

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>The last values of one measure, oldest first, for the charts.</summary>
internal sealed class History(int capacity)
{
    private readonly Queue<double?> _values = new();

    public IReadOnlyList<double?> Values => [.. _values];

    public void Add(double? value)
    {
        _values.Enqueue(value);
        while (_values.Count > capacity)
        {
            _values.Dequeue();
        }
    }
}

/// <summary>Text charts for the quick view (Ctrl+Q): sparklines and bars from block characters.</summary>
internal static class TextChart
{
    private const string Levels = "▁▂▃▄▅▆▇█";

    /// <summary>One character per value, scaled 0..<paramref name="max"/>; a missing value is a space.</summary>
    public static string Sparkline(IReadOnlyList<double?> values, double max = 100)
    {
        var text = new StringBuilder(values.Count);
        foreach (var value in values)
        {
            if (value is not { } v || max <= 0)
            {
                text.Append(' ');
                continue;
            }

            var level = (int)Math.Round(Math.Clamp(v / max, 0, 1) * (Levels.Length - 1));
            text.Append(Levels[level]);
        }

        return text.ToString();
    }

    /// <summary>"[█████░░░░░] 52%": a bar of <paramref name="width"/> cells for 0–100.</summary>
    public static string Bar(double? percent, int width = 20)
    {
        if (percent is not { } p)
        {
            return "[" + new string(' ', width) + "] n/a";
        }

        var filled = (int)Math.Round(Math.Clamp(p, 0, 100) / 100 * width);
        return "[" + new string('█', filled) + new string('░', width - filled) + "] " + p.ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    public static string Value(double? value, string unit, string format = "0") =>
        value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) + unit : "n/a";
}
