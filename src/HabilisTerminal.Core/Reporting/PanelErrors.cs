using System.Globalization;
using System.Text;

namespace HabilisTerminal.Core.Reporting;

/// <summary>One message a panel showed in red: when, which panel, the words.</summary>
public sealed record PanelError(DateTime Time, string Panel, string Message);

/// <summary>
/// The red messages panels show (issue #292), kept so they can be read later and sent with a report: they are
/// otherwise gone once the panel moves on. The log is a local file of the last <see cref="Kept"/> messages, one per
/// line, as they were; the reports hide paths and the user's name (<see cref="ReportMasking"/>).
/// </summary>
public static class PanelErrors
{
    public const int Kept = 200;

    /// <summary>How many of the latest go into a report.</summary>
    public const int InReport = 20;

    private static readonly Lock Gate = new();
    private static readonly List<PanelError> Errors = [];
    private static string? _file;

    /// <summary>The log file; its lines are read once, then each new message is appended.</summary>
    public static void Initialize(string file)
    {
        lock (Gate)
        {
            _file = file;
            Errors.Clear();
            try
            {
                if (File.Exists(file))
                {
                    Errors.AddRange(File.ReadLines(file).Select(Parse).OfType<PanelError>().TakeLast(Kept));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that cannot be read starts empty.
            }
        }
    }

    public static string? LogFile
    {
        get
        {
            lock (Gate)
            {
                return _file;
            }
        }
    }

    /// <summary>Keeps a message; the same one from the same panel again within a few seconds counts once.</summary>
    public static void Record(string panel, string message, DateTime? now = null)
    {
        var text = OneLine(message);
        if (text.Length == 0)
        {
            return;
        }

        var error = new PanelError(now ?? DateTime.Now, panel, text);
        lock (Gate)
        {
            if (Errors.Count > 0 && Errors[^1] is { } last && last.Panel == panel && last.Message == text && (error.Time - last.Time).Duration() < TimeSpan.FromSeconds(5))
            {
                return;
            }

            Errors.Add(error);
            var trimmed = Errors.Count > Kept;
            if (trimmed)
            {
                Errors.RemoveRange(0, Errors.Count - Kept);
            }

            Save(error, rewrite: trimmed);
        }
    }

    public static IReadOnlyList<PanelError> Recent(int count = InReport)
    {
        lock (Gate)
        {
            return [.. Errors.TakeLast(count)];
        }
    }

    /// <summary>The report's section on the latest messages, masked; empty when there were none.</summary>
    public static string Section(ReportMasking masking, string heading = "## Panel errors", int count = InReport)
    {
        var recent = Recent(count);
        if (recent.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder()
            .AppendLine(heading)
            .AppendLine(CultureInfo.InvariantCulture, $"The last {recent.Count} messages panels showed in red, newest last.")
            .AppendLine()
            .AppendLine("| Time | Panel | Message |")
            .AppendLine("|---|---|---|");
        foreach (var error in recent)
        {
            var message = ReportMasking.HidePaths(masking.Mask(error.Message)).Replace("|", "\\|", StringComparison.Ordinal);
            text.AppendLine(CultureInfo.InvariantCulture, $"| {error.Time:yyyy-MM-dd HH:mm:ss} | {error.Panel} | {message} |");
        }

        return text.ToString();
    }

    internal static string Format(PanelError error) =>
        string.Create(CultureInfo.InvariantCulture, $"{error.Time:yyyy-MM-ddTHH:mm:ss}\t{error.Panel}\t{error.Message}");

    internal static PanelError? Parse(string line)
    {
        var parts = line.Split('\t', 3);
        return parts.Length == 3 && DateTime.TryParseExact(parts[0], "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? new PanelError(time, parts[1], parts[2])
            : null;
    }

    private static string OneLine(string message) =>
        string.Join(' ', message.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static void Save(PanelError error, bool rewrite)
    {
        if (_file is not { } file)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (rewrite)
            {
                File.WriteAllLines(file, Errors.Select(Format));
            }
            else
            {
                File.AppendAllText(file, Format(error) + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The message is still in memory and in the next report; the file catches up on the next rewrite.
        }
    }
}
