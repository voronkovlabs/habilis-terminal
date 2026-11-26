using Avalonia;
using Velopack;

namespace HabilisTerminal.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The helper of performance diagnostics (issue #164): no window, no install hooks, and it ends with its parent.
        if (OperatingSystem.IsWindows() && args is ["--perf-stack-sampler", var parent] && int.TryParse(parent, out var parentId))
        {
            Core.Diagnostics.ThreadStacks.RunHelper(parentId, Console.In, Console.Out);
            return;
        }

        // "habi mcp" (issue #387): an MCP server over stdio for Claude, driving the running app. No window.
        if (args is ["mcp"])
        {
            RunMcp();
            return;
        }

        // Install / update / uninstall hooks run here and may exit the process (SPEC §15).
        VelopackApp.Build().Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void RunMcp()
    {
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n", AutoFlush = true };
        var client = new Core.Control.ControlClient();
        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0";
        var server = new Core.Control.McpServer((method, parameters) => method switch
        {
            // Two windows (issue #387): which one the tools drive is this session's choice.
            "mcp.apps" => Task.FromResult<System.Text.Json.Nodes.JsonNode?>(System.Text.Json.JsonSerializer.SerializeToNode(
                client.Apps().Select(a => new { processId = a.ProcessId, started = a.Started, current = a.Current }))),
            "mcp.use" => parameters["processId"]?.GetValue<int>() is { } pid && client.Use(pid)
                ? Task.FromResult<System.Text.Json.Nodes.JsonNode?>(new System.Text.Json.Nodes.JsonObject { ["processId"] = pid })
                : throw new Core.Control.ControlCallException("not_found", "No Habilis Terminal runs with that process id; list_apps shows them."),
            _ => client.CallAsync(method, parameters),
        }, version);
        server.RunAsync(input, output).GetAwaiter().GetResult();
    }

    /// <summary>The Avalonia application (issue #253); also what the previewer would use.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
