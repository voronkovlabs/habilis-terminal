using System.Text.Json;
using System.Text.Json.Nodes;
using HabilisTerminal.Core.Control;
using HabilisTerminal.Core.Plugins;

namespace HabilisTerminal.Core.Tests;

/// <summary>Issue #387: the control methods of the local API and the MCP server over them.</summary>
public class ControlApiTests
{
    private sealed class NoPlugins : IPluginApiHandler
    {
        public Task<IReadOnlyList<PluginApiEntry>> ListAsync() => Task.FromResult<IReadOnlyList<PluginApiEntry>>([]);

        public Task<PluginApiOutcome> LoadAsync(string folder) => throw new NotSupportedException();

        public Task<PluginApiOutcome> ReloadAsync(string id) => throw new NotSupportedException();

        public Task<PluginApiOutcome> UnloadAsync(string id) => throw new NotSupportedException();

        public Task<PluginApiPanelState> OpenPanelAsync(string target) => throw new NotSupportedException();

        public Task<PluginApiPanelState> PanelStateAsync() => throw new NotSupportedException();

        public Task<IReadOnlyList<PluginApiCommand>> ListCommandsAsync() => Task.FromResult<IReadOnlyList<PluginApiCommand>>([]);
    }

    private sealed class FakeControl : IControlApiHandler
    {
        public ControlMode Mode { get; set; } = ControlMode.Trusted;

        public List<string> Calls { get; } = [];

        private static ControlPanel Panel(string side) => new(side, side == "left", true, @"C:\work", null, null, null, "a.txt", ["a.txt"], 1, [new ControlRow("a.txt", false, 3, null, true)]);

        public Task<ControlState> StateAsync(int maxRows)
        {
            Calls.Add($"state {maxRows}");
            return Task.FromResult(new ControlState("left", Panel("left"), Panel("right"), null, @"C:\work", "Idle", 1));
        }

        public Task<ControlPanel> PanelAsync(string? side, int maxRows) => Task.FromResult(Panel(side ?? "left"));

        public Task<ControlPanel> NavigateAsync(string? side, string target, int maxRows)
        {
            Calls.Add($"navigate {side} {target}");
            return Task.FromResult(Panel(side ?? "left"));
        }

        public Task<ControlPanel> ActivateAsync(string side, int maxRows) => Task.FromResult(Panel(side));

        public Task<ControlPanel> CursorAsync(string? side, string name, int maxRows) => Task.FromResult(Panel("left"));

        public Task<ControlPanel> SelectAsync(string? side, IReadOnlyList<string> names, string mode, int maxRows)
        {
            Calls.Add($"select {mode} {string.Join(",", names)}");
            return Task.FromResult(Panel("left"));
        }

        public Task RunCommandAsync(string id)
        {
            Calls.Add("run " + id);
            return Task.CompletedTask;
        }

        public Task<ControlConsoleResult> ConsoleRunAsync(string command, int timeoutMs, bool ask)
        {
            Calls.Add($"console {command} ask={ask}");
            return Task.FromResult(new ControlConsoleResult(true, 0, "out"));
        }

        public Task<string> ConsoleReadAsync(int lines) => Task.FromResult("text");

        public Task<ControlState> OpenFileAsync(string path, bool edit) => StateAsync(0);

        public Task<ControlState> ClosePageAsync() => StateAsync(0);

        public Task<ControlScreenshot> ScreenshotAsync() => Task.FromResult(new ControlScreenshot(1, 1, "iVBOR"));

        public Task<IReadOnlyList<ControlSetting>> SettingsAsync(string? key) => Task.FromResult<IReadOnlyList<ControlSetting>>([]);

        public Task<ControlSetting> SetSettingAsync(string key, JsonElement value, bool ask) =>
            throw new PluginApiException(PluginApiException.Declined, "The user declined the change.");
    }

    private readonly FakeControl _control = new();

    private async Task<JsonElement> Ask(string line, string? assistantToken = null)
    {
        using var server = new PluginApiServer("unused", new NoPlugins(), new Version(1, 0, 0), _control) { AssistantToken = assistantToken };
        return JsonDocument.Parse(await server.HandleLineAsync(line)).RootElement;
    }

    [Fact]
    public async Task The_assistant_secret_opens_control_without_asking_again()
    {
        // #197: the assistant page asked the user already; its calls run with control off, and Ask mode asks nothing.
        _control.Mode = ControlMode.Off;
        var run = await Ask("""{ "method": "console.run", "token": "s3cret", "params": { "command": "dir" } }""", "s3cret");
        Assert.True(run.GetProperty("ok").GetBoolean());

        _control.Mode = ControlMode.Ask;
        await Ask("""{ "method": "console.run", "token": "s3cret", "params": { "command": "ls" } }""", "s3cret");
        await Ask("""{ "method": "console.run", "params": { "command": "pwd" } }""", "s3cret");

        _control.Mode = ControlMode.Off;
        var wrong = await Ask("""{ "method": "console.run", "token": "guess", "params": { "command": "x" } }""", "s3cret");
        var none = await Ask("""{ "method": "console.run", "token": "s3cret", "params": { "command": "y" } }""");

        Assert.Equal(["console dir ask=False", "console ls ask=False", "console pwd ask=True"], _control.Calls);
        Assert.Equal(PluginApiException.Disabled, wrong.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(PluginApiException.Disabled, none.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Control_methods_reach_the_handler_with_their_parameters()
    {
        var state = await Ask("""{ "id": 1, "method": "app.state", "params": { "rows": 5 } }""");
        Assert.True(state.GetProperty("ok").GetBoolean());
        Assert.Equal("a.txt", state.GetProperty("result").GetProperty("left").GetProperty("cursor").GetString());

        await Ask("""{ "method": "panels.navigate", "params": { "side": "right", "target": "s3://b/" } }""");
        await Ask("""{ "method": "panels.select", "params": { "names": ["a", "b"], "mode": "add" } }""");
        var run = await Ask("""{ "method": "commands.run", "params": { "id": "files.copy" } }""");

        Assert.Equal(["state 5", "navigate right s3://b/", "select add a,b", "run files.copy"], _control.Calls);
        Assert.Equal("files.copy", run.GetProperty("result").GetProperty("ran").GetString());
    }

    [Fact]
    public async Task Control_is_refused_while_it_is_off_and_errors_keep_their_code()
    {
        _control.Mode = ControlMode.Off;
        var off = await Ask("""{ "method": "app.state" }""");
        Assert.Equal(PluginApiException.Disabled, off.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_control.Calls);

        _control.Mode = ControlMode.Ask;
        var declined = await Ask("""{ "method": "settings.set", "params": { "key": "panels.fontSize", "value": 14 } }""");
        Assert.Equal(PluginApiException.Declined, declined.GetProperty("error").GetProperty("code").GetString());

        var missing = await Ask("""{ "method": "settings.set", "params": { "key": "panels.fontSize" } }""");
        Assert.Equal(PluginApiException.BadRequest, missing.GetProperty("error").GetProperty("code").GetString());
    }

    private static McpServer Mcp(List<(string Method, JsonObject Params)> calls) => new((method, parameters) =>
    {
        calls.Add((method, parameters));
        return method switch
        {
            "window.screenshot" => Task.FromResult<JsonNode?>(new JsonObject { ["png"] = "iVBOR", ["width"] = 1, ["height"] = 1 }),
            "commands.run" => throw new ControlCallException("not_found", "There is no command \"x\"."),
            _ => Task.FromResult<JsonNode?>(new JsonObject { ["fine"] = true }),
        };
    }, "1.2.3");

    [Fact]
    public async Task Mcp_initializes_lists_tools_and_answers_no_notification()
    {
        var server = Mcp([]);
        var init = JsonNode.Parse((await server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}"""))!)!;
        Assert.Equal("2025-03-26", init["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("habilis-terminal", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.NotNull(init["result"]!["capabilities"]!["tools"]);

        Assert.Null(await server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));

        var list = JsonNode.Parse((await server.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""))!)!;
        var names = list["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("get_state", names);
        Assert.Contains("run_in_console", names);
        Assert.Contains("screenshot", names);
        Assert.All(list["result"]!["tools"]!.AsArray(), t => Assert.Equal("object", t!["inputSchema"]!["type"]!.GetValue<string>()));

        var unknown = JsonNode.Parse((await server.HandleAsync("""{"jsonrpc":"2.0","id":3,"method":"nope"}"""))!)!;
        Assert.Equal(-32601, unknown["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Mcp_tools_call_the_api_and_report_errors_as_tool_errors()
    {
        var calls = new List<(string Method, JsonObject Params)>();
        var server = Mcp(calls);

        var navigate = JsonNode.Parse((await server.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"navigate","arguments":{"target":"C:\\work","side":"left"}}}"""))!)!;
        Assert.Equal("panels.navigate", calls[0].Method);
        Assert.Equal(@"C:\work", calls[0].Params["target"]!.GetValue<string>());
        Assert.Equal("text", navigate["result"]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Null(navigate["result"]!["isError"]);

        var shot = JsonNode.Parse((await server.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"screenshot"}}"""))!)!;
        Assert.Equal("image", shot["result"]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("image/png", shot["result"]!["content"]![0]!["mimeType"]!.GetValue<string>());

        var failed = JsonNode.Parse((await server.HandleAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"run_command","arguments":{"id":"x"}}}"""))!)!;
        Assert.True(failed["result"]!["isError"]!.GetValue<bool>());
        Assert.StartsWith("not_found:", failed["result"]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
    }
}
