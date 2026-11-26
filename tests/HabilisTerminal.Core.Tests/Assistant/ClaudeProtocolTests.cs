using System.Text.Json.Nodes;
using HabilisTerminal.Core.Assistant;

namespace HabilisTerminal.Core.Tests.Assistant;

/// <summary>Issue #197: Claude Code's stream-json lines as the assistant page sees them, and the lines it writes back.</summary>
public class ClaudeProtocolTests
{
    [Fact]
    public void Init_text_tools_results_and_the_end_of_a_turn_become_events()
    {
        Assert.Equal(new AssistantStarted("s1", "claude-haiku-5-5"),
            Assert.Single(ClaudeProtocol.Parse("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-haiku-5-5","tools":[]}""")));

        var turn = ClaudeProtocol.Parse("""
            {"type":"assistant","parent_tool_use_id":null,"message":{"content":[{"type":"text","text":"Looking."},{"type":"tool_use","id":"t1","name":"PowerShell","input":{"command":"Get-ChildItem\n-File","description":"list"}}]}}
            """);
        Assert.Equal([new AssistantText("Looking."), new AssistantToolUse("t1", "PowerShell", "Get-ChildItem -File")], turn);

        var result = Assert.IsType<AssistantToolResult>(Assert.Single(ClaudeProtocol.Parse(
            """{"type":"user","parent_tool_use_id":null,"message":{"content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":[{"type":"text","text":"Access denied"}]}]}}""")));
        Assert.Equal(new AssistantToolResult("t1", true, "Access denied"), result);

        var ended = Assert.IsType<AssistantTurnEnded>(Assert.Single(ClaudeProtocol.Parse(
            """{"type":"result","subtype":"success","is_error":false,"result":"Done.","total_cost_usd":0.0123,"duration_ms":4200}""")));
        Assert.Equal(new AssistantTurnEnded(false, "Done.", 0.0123, 4200), ended);
    }

    [Fact]
    public void Subagent_messages_hooks_and_noise_are_left_out()
    {
        Assert.Empty(ClaudeProtocol.Parse("""{"type":"assistant","parent_tool_use_id":"t9","message":{"content":[{"type":"text","text":"inner"}]}}"""));
        Assert.Empty(ClaudeProtocol.Parse("""{"type":"system","subtype":"hook_started"}"""));
        Assert.Empty(ClaudeProtocol.Parse("   "));
        Assert.Equal(new AssistantProblem("not json"), Assert.Single(ClaudeProtocol.Parse("not json")));
    }

    [Fact]
    public void A_permission_request_is_answered_with_its_id_and_the_same_input()
    {
        var asks = Assert.IsType<AssistantAsks>(Assert.Single(ClaudeProtocol.Parse(
            """{"type":"control_request","request_id":"r7","request":{"subtype":"can_use_tool","tool_name":"Write","input":{"file_path":"C:\\t\\a.txt","content":"hi"}}}""")));
        Assert.Equal("r7", asks.RequestId);
        Assert.Equal("Write", asks.Tool);
        Assert.Equal(@"C:\t\a.txt", asks.Summary);

        var allow = JsonNode.Parse(ClaudeProtocol.Allow(asks))!;
        Assert.Equal("control_response", (string?)allow["type"]);
        Assert.Equal("r7", (string?)allow["response"]!["request_id"]);
        Assert.Equal("allow", (string?)allow["response"]!["response"]!["behavior"]);
        Assert.Equal("hi", (string?)allow["response"]!["response"]!["updatedInput"]!["content"]);

        var deny = JsonNode.Parse(ClaudeProtocol.Deny(asks, "No."))!;
        Assert.Equal("deny", (string?)deny["response"]!["response"]!["behavior"]);
        Assert.Equal("No.", (string?)deny["response"]!["response"]!["message"]);
    }

    [Fact]
    public void A_user_message_and_an_interrupt_are_single_lines()
    {
        var user = ClaudeProtocol.UserMessage("keep only\nthe music");
        Assert.DoesNotContain('\n', user);
        Assert.Equal("keep only\nthe music", (string?)JsonNode.Parse(user)!["message"]!["content"]);
        Assert.Equal("interrupt", (string?)JsonNode.Parse(ClaudeProtocol.Interrupt())!["request"]!["subtype"]);
    }

    [Fact]
    public void The_launch_asks_the_host_for_permissions_and_drives_this_window()
    {
        var options = new ClaudeLaunchOptions(@"C:\tools\claude.cmd", Path.GetTempPath(), @"C:\app\habi.exe", 1234, "tok",
            AssistantProvider.Bedrock, "sonnet", "work", "eu-west-1");
        var (start, config) = ClaudeLaunch.Build(options);
        try
        {
            var arguments = start.ArgumentList.ToList();
            Assert.Equal(["/d", "/c", @"C:\tools\claude.cmd", "-p"], arguments.Take(4));
            Assert.Equal("host", arguments[arguments.IndexOf("--permission-prompts") + 1]);
            Assert.Equal("stream-json", arguments[arguments.IndexOf("--input-format") + 1]);
            Assert.Equal(config, arguments[arguments.IndexOf("--mcp-config") + 1]);
            Assert.Equal("sonnet", arguments[arguments.IndexOf("--model") + 1]);
            Assert.Contains("mcp__habilis__get_state", arguments[arguments.IndexOf("--allowedTools") + 1]);
            Assert.Equal("1234", start.Environment["HABI_PID"]);
            Assert.Equal("tok", start.Environment["HABI_ASSISTANT_TOKEN"]);
            Assert.Equal("1", start.Environment["CLAUDE_CODE_USE_BEDROCK"]);
            Assert.Equal("work", start.Environment["AWS_PROFILE"]);
            Assert.Equal("eu-west-1", start.Environment["AWS_REGION"]);
            var mcp = JsonNode.Parse(File.ReadAllText(config))!;
            Assert.Equal(@"C:\app\habi.exe", (string?)mcp["mcpServers"]!["habilis"]!["command"]);
            Assert.Equal("mcp", (string?)mcp["mcpServers"]!["habilis"]!["args"]![0]);
        }
        finally
        {
            File.Delete(config);
        }

        var (plain, plainConfig) = ClaudeLaunch.Build(options with { Claude = @"C:\tools\claude.exe", Provider = AssistantProvider.ClaudeCode, Model = null });
        File.Delete(plainConfig);
        Assert.Equal(@"C:\tools\claude.exe", plain.FileName);
        Assert.False(plain.Environment.ContainsKey("CLAUDE_CODE_USE_BEDROCK"));
        Assert.DoesNotContain("--model", plain.ArgumentList);
    }
}
