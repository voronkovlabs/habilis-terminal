using System.Text.Json;
using System.Text.Json.Nodes;

namespace HabilisTerminal.Core.Control;

/// <summary>A failed call of the app's API, with its code (docs/plugin-api.md).</summary>
public sealed class ControlCallException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The MCP server <c>habi mcp</c> runs (issue #387): JSON-RPC 2.0 over stdio, one message per line. Each tool is one call
/// of the app's control API, so Claude Code or Claude Desktop can drive the running app.
/// </summary>
/// <param name="call">Calls an API method with its parameters; returns the result or throws <see cref="ControlCallException"/>.</param>
public sealed class McpServer(Func<string, JsonObject, Task<JsonNode?>> call, string version)
{
    public const string LatestProtocol = "2025-06-18";

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private sealed record Tool(string Name, string Description, string Method, JsonObject Schema, Func<JsonObject, JsonObject>? Map = null);

    private static readonly Tool[] Tools =
    [
        new("get_state", "Both panels (location, cursor, marked names, first rows), the page open over them (viewer, editor, settings) and the console's folder and state. Start here.", "app.state",
            Schema(("rows", Integer("Rows per panel to list, 200 by default")))),
        new("get_panel", "One panel in detail: its location, cursor, marked names and rows.", "panels.get",
            Schema(("side", Side()), ("rows", Integer("Rows to list, 200 by default")))),
        new("navigate", "Opens a folder, a file's folder (cursor on the file) or a plugin address (s3://bucket/, sqs:) in a panel, as the user would.", "panels.navigate",
            Schema(["target"], ("target", Text("A full path or a plugin address")), ("side", Side()))),
        new("activate_panel", "Makes the left or right panel the active one.", "panels.activate", Schema(["side"], ("side", Side()))),
        new("set_cursor", "Puts the cursor on a name in a panel.", "panels.cursor", Schema(["name"], ("name", Text("The name")), ("side", Side()))),
        new("select", "Marks names in a panel, as Insert does: mode set (only these), add, remove or clear.", "panels.select",
            Schema(("names", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }), ("mode", Enum("set", "add", "remove", "clear")), ("side", Side()))),
        new("list_commands", "Every command the app has now (the Ctrl+Shift+P list): id, title, section, keys. Run one with run_command.", "commands.list", Schema()),
        new("run_command", "Runs a command by id from list_commands, as if the user chose it (copy, move, delete, view modes, sort, find...). The app's own confirmations still appear for the user.", "commands.run",
            Schema(["id"], ("id", Text("The command id, e.g. files.copy")))),
        new("run_in_console", "Runs a command line in the app's console (PowerShell) and waits for it to finish; returns the exit code and what it printed.", "console.run",
            Schema(["command"], ("command", Text("The command line")), ("timeoutMs", Integer("How long to wait, 60000 by default")))),
        new("read_console", "The console's last lines of text.", "console.read", Schema(("lines", Integer("How many lines, 100 by default")))),
        new("view_file", "Opens a file in the app's viewer (F3).", "files.view", Schema(["path"], ("path", Text("A full path")))),
        new("edit_file", "Opens a file in the app's editor (F4).", "files.edit", Schema(["path"], ("path", Text("A full path")))),
        new("close_page", "Closes the viewer, editor or settings page over the panels.", "pages.close", Schema()),
        new("screenshot", "A picture of the app's window as it is now.", "window.screenshot", Schema()),
        new("get_settings", "The app's settings with their values, or one by key.", "settings.get", Schema(("key", Text("A key such as panels.fontSize")))),
        new("set_setting", "Changes one setting, e.g. panels.fontSize to 14.", "settings.set",
            Schema(["key", "value"], ("key", Text("The key")), ("value", new JsonObject { ["description"] = "The new value: a string, number or boolean" }))),
    ];

    /// <summary>Serves until the input ends.</summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (await HandleAsync(line).ConfigureAwait(false) is { } reply)
            {
                await output.WriteLineAsync(reply).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One message to its reply; null for a notification. Never throws.</summary>
    public async Task<string?> HandleAsync(string line)
    {
        JsonNode? id = null;
        try
        {
            var message = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("not an object");
            id = message["id"]?.DeepClone();
            var method = message["method"]?.GetValue<string>();
            var parameters = message["params"] as JsonObject ?? [];
            if (id is null)
            {
                return null; // notifications/initialized and the like
            }

            JsonNode? result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = new JsonArray([.. Tools.Select(Describe)]) },
                "tools/call" => await CallToolAsync(parameters).ConfigureAwait(false),
                _ => null,
            };
            return result is null && method is not ("ping")
                ? Error(id, -32601, $"Method not found: {method}")
                : Reply(id, result);
        }
        catch (JsonException ex)
        {
            return Error(id, -32700, $"Parse error: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Error(id, -32603, ex.Message);
        }
    }

    private JsonObject Initialize(JsonObject parameters) => new()
    {
        ["protocolVersion"] = parameters["protocolVersion"]?.GetValue<string>() ?? LatestProtocol,
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = "habilis-terminal", ["version"] = version },
        ["instructions"] = "Drives the running Habilis Terminal, a two-panel file manager with a console. Call get_state first. Paths are full Windows paths. Destructive commands show the app's own confirmation to the user.",
    };

    private async Task<JsonObject> CallToolAsync(JsonObject parameters)
    {
        var name = parameters["name"]?.GetValue<string>();
        var tool = Tools.FirstOrDefault(t => t.Name == name);
        if (tool is null)
        {
            return ToolError($"There is no tool \"{name}\".");
        }

        var arguments = parameters["arguments"] as JsonObject ?? [];
        try
        {
            var result = await call(tool.Method, (JsonObject)arguments.DeepClone()).ConfigureAwait(false);
            if (tool.Method == "window.screenshot" && result?["png"]?.GetValue<string>() is { } png)
            {
                return new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "image", ["data"] = png, ["mimeType"] = "image/png" }),
                };
            }

            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result?.ToJsonString(Compact) ?? "{}" }),
            };
        }
        catch (ControlCallException ex)
        {
            return ToolError($"{ex.Code}: {ex.Message}");
        }
    }

    private static JsonObject ToolError(string message) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        ["isError"] = true,
    };

    private static JsonObject Describe(Tool tool) => new()
    {
        ["name"] = tool.Name,
        ["description"] = tool.Description,
        ["inputSchema"] = tool.Schema.DeepClone(),
    };

    private static string Reply(JsonNode id, JsonNode? result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() }.ToJsonString(Compact);

    private static string Error(JsonNode? id, int code, string message) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString(Compact);

    private static JsonObject Schema(params (string Name, JsonObject Property)[] properties) => Schema([], properties);

    private static JsonObject Schema(string[] required, params (string Name, JsonObject Property)[] properties)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject([.. properties.Select(p => KeyValuePair.Create(p.Name, (JsonNode?)p.Property))]) };
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]);
        }

        return schema;
    }

    private static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Integer(string description) => new() { ["type"] = "integer", ["description"] = description };

    private static JsonObject Side() => Enum("left", "right");

    private static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]) };
}
