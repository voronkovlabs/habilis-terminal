using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using HabilisTerminal.Core.Plugins;

namespace HabilisTerminal.Core.Control;

/// <summary>
/// The client side of the app's API for <c>habi mcp</c> (issue #387): one connection per call, so a restarted app is
/// found again. The app is the one in <c>HABI_PID</c> (set in the app's own consoles), else the newest running habi.
/// </summary>
public sealed class ControlClient
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private int _next;

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

    private static int? FindApp()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("HABI_PID"), out var given) && IsRunning(given))
        {
            return given;
        }

        var self = Environment.ProcessId;
        return Process.GetProcessesByName("habi")
            .Where(p => p.Id != self)
            .Select(p => (p.Id, Started: StartTime(p)))
            .OrderByDescending(p => p.Started)
            .Select(p => (int?)p.Id)
            .FirstOrDefault();
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
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
