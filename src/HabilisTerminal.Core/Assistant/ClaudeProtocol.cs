using System.Text.Json;
using System.Text.Json.Nodes;

namespace HabilisTerminal.Core.Assistant;

/// <summary>Something Claude Code reported in a session (issue #197): what the assistant page shows.</summary>
public abstract record AssistantEvent;

/// <summary>The session started: its id and the model that answers.</summary>
public sealed record AssistantStarted(string SessionId, string? Model) : AssistantEvent;

/// <summary>Text the assistant wrote.</summary>
public sealed record AssistantText(string Text) : AssistantEvent;

/// <summary>The assistant uses a tool: which, and a short line saying on what.</summary>
public sealed record AssistantToolUse(string Id, string Tool, string Summary) : AssistantEvent;

/// <summary>What a tool gave back (shortened).</summary>
public sealed record AssistantToolResult(string ToolUseId, bool IsError, string Text) : AssistantEvent;

/// <summary>Claude Code asks whether a tool may run. Answer with <see cref="ClaudeProtocol.Allow"/> or <see cref="ClaudeProtocol.Deny"/>.</summary>
public sealed record AssistantAsks(string RequestId, string Tool, string Summary, JsonObject Input) : AssistantEvent;

/// <summary>A turn ended: whether it failed, its text, and what it cost when Claude Code knows.</summary>
public sealed record AssistantTurnEnded(bool IsError, string? Text, double? CostUsd, long? DurationMs) : AssistantEvent;

/// <summary>Claude Code wrote to its error stream, or ended.</summary>
public sealed record AssistantProblem(string Text) : AssistantEvent;

/// <summary>
/// Claude Code's stream-json protocol (<c>claude -p --input-format stream-json --output-format stream-json</c>): one JSON
/// object per line each way. The lines the app reads become <see cref="AssistantEvent"/>s; the lines it writes are a user
/// message, an answer to a permission request, or an interrupt.
/// </summary>
public static class ClaudeProtocol
{
    private const int MaxResultText = 2000;
    private static int _nextRequest;

    /// <summary>The events in one line of Claude Code's output; none for lines the page has no use for.</summary>
    public static IReadOnlyList<AssistantEvent> Parse(string line)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return line.Trim().Length > 0 ? [new AssistantProblem(line.Trim())] : [];
        }

        if (message is null)
        {
            return [];
        }

        switch (Text(message, "type"))
        {
            case "system" when Text(message, "subtype") == "init":
                return [new AssistantStarted(Text(message, "session_id") ?? string.Empty, Text(message, "model"))];

            case "assistant" when message["parent_tool_use_id"] is null:
                return [.. Content(message).SelectMany(AssistantBlock)];

            case "user" when message["parent_tool_use_id"] is null:
                return [.. Content(message).SelectMany(ResultBlock)];

            case "result":
                return
                [
                    new AssistantTurnEnded(
                        message["is_error"]?.GetValue<bool>() ?? Text(message, "subtype") != "success",
                        Text(message, "result"),
                        Number(message, "total_cost_usd"),
                        (long?)Number(message, "duration_ms")),
                ];

            case "control_request" when message["request"] is JsonObject request && Text(request, "subtype") == "can_use_tool":
                var tool = Text(request, "tool_name") ?? "tool";
                var input = request["input"] as JsonObject ?? [];
                return [new AssistantAsks(Text(message, "request_id") ?? string.Empty, tool, Summarize(tool, input), input)];

            default:
                return [];
        }
    }

    /// <summary>A user message.</summary>
    public static string UserMessage(string text) => new JsonObject
    {
        ["type"] = "user",
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
    }.ToJsonString();

    /// <summary>Lets the tool run with the input it asked for.</summary>
    public static string Allow(AssistantAsks permission) => Response(permission.RequestId, new JsonObject
    {
        ["behavior"] = "allow",
        ["updatedInput"] = permission.Input.DeepClone(),
    });

    /// <summary>Refuses the tool; the message tells the assistant why.</summary>
    public static string Deny(AssistantAsks permission, string message) => Response(permission.RequestId, new JsonObject
    {
        ["behavior"] = "deny",
        ["message"] = message,
    });

    /// <summary>Stops the turn under way, as Esc does in Claude Code.</summary>
    public static string Interrupt() => new JsonObject
    {
        ["type"] = "control_request",
        ["request_id"] = $"habi-{Interlocked.Increment(ref _nextRequest)}",
        ["request"] = new JsonObject { ["subtype"] = "interrupt" },
    }.ToJsonString();

    /// <summary>A line about what a tool is about to do: the command, the file, the pattern.</summary>
    public static string Summarize(string tool, JsonObject input)
    {
        foreach (var key in (string[])["command", "file_path", "path", "pattern", "url", "query", "target", "description", "prompt"])
        {
            if (Text(input, key) is { Length: > 0 } value)
            {
                return OneLine(value);
            }
        }

        var json = input.ToJsonString();
        return json == "{}" ? string.Empty : OneLine(json);
    }

    private static string Response(string requestId, JsonObject response) => new JsonObject
    {
        ["type"] = "control_response",
        ["response"] = new JsonObject
        {
            ["subtype"] = "success",
            ["request_id"] = requestId,
            ["response"] = response,
        },
    }.ToJsonString();

    private static IEnumerable<JsonObject> Content(JsonObject message) =>
        message["message"]?["content"] is JsonArray blocks ? blocks.OfType<JsonObject>() : [];

    private static IEnumerable<AssistantEvent> AssistantBlock(JsonObject block)
    {
        switch (Text(block, "type"))
        {
            case "text" when Text(block, "text") is { Length: > 0 } text:
                yield return new AssistantText(text);
                break;
            case "tool_use":
                var tool = Text(block, "name") ?? "tool";
                yield return new AssistantToolUse(Text(block, "id") ?? string.Empty, tool, Summarize(tool, block["input"] as JsonObject ?? []));
                break;
        }
    }

    private static IEnumerable<AssistantEvent> ResultBlock(JsonObject block)
    {
        if (Text(block, "type") != "tool_result")
        {
            yield break;
        }

        var text = block["content"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var s) => s,
            JsonArray parts => string.Join("\n", parts.OfType<JsonObject>().Select(p => Text(p, "text")).Where(t => t is not null)),
            _ => string.Empty,
        };
        if (text.Length > MaxResultText)
        {
            text = string.Concat(text.AsSpan(0, MaxResultText), "…");
        }

        yield return new AssistantToolResult(Text(block, "tool_use_id") ?? string.Empty, block["is_error"]?.GetValue<bool>() ?? false, text);
    }

    private static string OneLine(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > 200 ? string.Concat(line.AsSpan(0, 200), "…") : line;
    }

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? Number(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
}
