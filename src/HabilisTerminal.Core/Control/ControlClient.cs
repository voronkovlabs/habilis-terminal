using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using HabilisTerminal.Core.Plugins;

namespace HabilisTerminal.Core.Control;

/// <summary>
/// The client side of the app's API for <c>habi mcp</c> (issue #387): one connection per call, so a restarted app is
/// found again. Which app: the one chosen with <see cref="Use"/>, else the one in <c>HABI_PID</c> (set in the app's own
/// consoles), else the newest running one. Only a habi that serves the API counts: <c>habi mcp</c> itself and the
/// diagnostics helper are habi processes too.
/// </summary>
public sealed class ControlClient
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private int _next;
    private int? _chosen;

    /// <summary>A running app: its process, when it started, and whether it is the one calls go to.</summary>
    public sealed record App(int ProcessId, DateTime Started, bool Current);

    /// <summary>The running apps, newest first.</summary>
    public IReadOnlyList<App> Apps()
    {
        var current = FindApp();
        return [.. Running().Select(a => new App(a.Id, a.Started, a.Id == current))];
    }

    /// <summary>Sends the calls of this session to one app; false when no such app runs.</summary>
    public bool Use(int processId)
    {
        if (!Running().Any(a => a.Id == processId))
        {
            return false;
        }

        _chosen = processId;
        return true;
    }

    public async Task<JsonNode?> CallAsync(string method, JsonObject parameters, CancellationToken cancellationToken = default)
    {
        var pid = FindApp() ?? throw new ControlCallException("no_app", "Habilis Terminal is not running. Start it, and turn on Settings, Claude Control.");
        await using var pipe = new NamedPipeClientStream(".", PluginApiServer.PipeNameFor(pid), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ControlCallException("no_app", $"Habilis Terminal (process {pid}) does not answer.");
        }

        using var reader = new StreamReader(pipe, Utf8, leaveOpen: true);
        var writer = new StreamWriter(pipe, Utf8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        await using (writer.ConfigureAwait(false))
        {
            var request = new JsonObject { ["id"] = Interlocked.Increment(ref _next), ["method"] = method, ["params"] = parameters };
            await writer.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new ControlCallException("no_app", "Habilis Terminal closed the connection.");
            var reply = JsonNode.Parse(line) as JsonObject ?? throw new ControlCallException("internal", "The reply is not a JSON object.");
            if (reply["ok"]?.GetValue<bool>() == true)
            {
                return reply["result"]?.DeepClone();
            }

            var error = reply["error"];
            throw new ControlCallException(error?["code"]?.GetValue<string>() ?? "internal", error?["message"]?.GetValue<string>() ?? "The call failed.");
        }
    }

    private int? FindApp()
    {
        var running = Running();
        if (_chosen is { } chosen && running.Any(a => a.Id == chosen))
        {
            return chosen;
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("HABI_PID"), out var given) && running.Any(a => a.Id == given))
        {
            return given;
        }

        return running.Count > 0 ? running[0].Id : null;
    }

    /// <summary>habi processes that serve the API, newest first.</summary>
    private static List<(int Id, DateTime Started)> Running()
    {
        var self = Environment.ProcessId;
        var pipes = Pipes();
        var list = new List<(int Id, DateTime Started)>();
        foreach (var process in Process.GetProcessesByName("habi"))
        {
            using (process)
            {
                if (process.Id != self && pipes.Contains(PluginApiServer.PipeNameFor(process.Id)))
                {
                    list.Add((process.Id, StartTime(process)));
                }
            }
        }

        return [.. list.OrderByDescending(a => a.Started)];
    }

    private static HashSet<string> Pipes()
    {
        try
        {
            return new HashSet<string>(Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static DateTime StartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return DateTime.MinValue;
        }
    }
}
