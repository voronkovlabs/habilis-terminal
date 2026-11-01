using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HabilisTerminal.Core.Reporting;

/// <summary>
/// What is known about a crash: written to a file when it happens and offered for sending on the next start.
/// <see cref="Signature"/> identifies the same bug across reports (exception type plus the top app frames).
/// </summary>
/// <param name="Source">Which handler caught it: <c>dispatcher</c>, <c>appdomain</c> or <c>task</c>.</param>
/// <param name="Details">The full exception chain with stack traces (<see cref="Exception.ToString"/>).</param>
/// <param name="Trace">The last lines of the HABI_TRACE file, if one was being written.</param>
public sealed record CrashReport(
    DateTimeOffset Time,
    string Source,
    string Version,
    string Os,
    string ExceptionType,
    string Message,
    string Details,
    string Signature,
    IReadOnlyList<string> Trace)
{
    private const string FilePrefix = "crash-";
    private const int SignatureFrames = 5;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static CrashReport FromException(Exception exception, string source, string version, string os, IReadOnlyList<string>? trace = null, DateTimeOffset? time = null)
    {
        var root = exception.GetBaseException();
        var type = root.GetType().FullName ?? root.GetType().Name;
        return new CrashReport(time ?? DateTimeOffset.Now, source, version, os, type, root.Message, exception.ToString(),
            ComputeSignature(type, root.StackTrace), trace ?? []);
    }

    /// <summary>
    /// A short hash of the exception type and the top frames of our own code (any frames when none are ours). Line
    /// numbers, parameters and compiler-generated lambda numbers are left out so the signature survives rebuilds.
    /// </summary>
    public static string ComputeSignature(string exceptionType, string? stackTrace)
    {
        var frames = (stackTrace ?? string.Empty).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("at ", StringComparison.Ordinal))
            .Select(NormalizeFrame)
            .ToList();
        var ours = frames.Where(frame => frame.StartsWith("HabilisTerminal.", StringComparison.Ordinal)).ToList();
        var top = (ours.Count > 0 ? ours : frames).Take(SignatureFrames);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(exceptionType + "\n" + string.Join("\n", top)));
        return Convert.ToHexStringLower(hash)[..12];
    }

    private static string NormalizeFrame(string line)
    {
        var frame = line[3..];
        var parameters = frame.IndexOf('(', StringComparison.Ordinal);
        if (parameters >= 0)
        {
            frame = frame[..parameters];
        }

        // <Method>b__12_0, <>c__DisplayClass5_0, <MethodAsync>d__7: the numbers change whenever the class changes.
        return Regex.Replace(frame, @"__\w*?\d+(_\d+)?", "__", RegexOptions.CultureInvariant);
    }

    /// <summary>Saves the report as a new file in <paramref name="directory"/>; returns its path.</summary>
    public string Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{FilePrefix}{Time.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        return path;
    }

    /// <summary>Reports waiting in <paramref name="directory"/>, newest first; unreadable files are skipped.</summary>
    public static IReadOnlyList<(string Path, CrashReport Report)> LoadPending(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var reports = new List<(string, CrashReport)>();
        foreach (var path in Directory.EnumerateFiles(directory, FilePrefix + "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<CrashReport>(File.ReadAllText(path)) is { } report)
                {
                    reports.Add((path, report));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        return [.. reports.OrderByDescending(entry => entry.Item2.Time)];
    }
}
