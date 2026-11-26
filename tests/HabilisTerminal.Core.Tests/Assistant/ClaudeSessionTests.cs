using System.Collections.Concurrent;
using System.Diagnostics;
using HabilisTerminal.Core.Assistant;

namespace HabilisTerminal.Core.Tests.Assistant;

/// <summary>Issue #197: a whole turn against a stand-in for Claude Code that asks for a permission and waits for the answer.</summary>
public class ClaudeSessionTests
{
    // Reads the user's message, asks whether Write may run, and reports what the host answered.
    private const string FakeClaude = """
        $in = [Console]::In
        $null = $in.ReadLine()
        [Console]::Out.WriteLine('{"type":"system","subtype":"init","session_id":"fake","model":"fake-model"}')
        [Console]::Out.WriteLine('{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Write","input":{"file_path":"a.txt"}}}')
        [Console]::Out.Flush()
        $answer = $in.ReadLine() | ConvertFrom-Json
        $behavior = $answer.response.response.behavior
        $text = "host said $behavior to $($answer.response.request_id)"
        [Console]::Out.WriteLine((@{ type = 'assistant'; parent_tool_use_id = $null; message = @{ content = @(@{ type = 'text'; text = $text }) } } | ConvertTo-Json -Compress -Depth 6))
        [Console]::Out.WriteLine('{"type":"result","subtype":"success","is_error":false,"result":"ok","duration_ms":5}')
        [Console]::Out.Flush()
        $null = $in.ReadLine()
        """;

    [Fact]
    public async Task A_turn_with_a_permission_request_round_trips()
    {
        var folder = Path.Combine(Path.GetTempPath(), "habi-tests", "assistant-session", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var script = Path.Combine(folder, "fake-claude.ps1");
        await File.WriteAllTextAsync(script, FakeClaude);
        try
        {
            var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-File", script])
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var events = new BlockingCollection<AssistantEvent>();
            using var session = ClaudeSession.Start(start);
            session.Event += item =>
            {
                if (item is AssistantAsks asks)
                {
                    session.Allow(asks);
                }

                events.Add(item);
            };

            session.Send("hello");
            var seen = new List<AssistantEvent>();
            while (events.TryTake(out var item, TimeSpan.FromSeconds(30)))
            {
                seen.Add(item);
                if (item is AssistantTurnEnded)
                {
                    break;
                }
            }

            Assert.Contains(new AssistantStarted("fake", "fake-model"), seen);
            Assert.Contains(seen, e => e is AssistantAsks { Tool: "Write", Summary: "a.txt" });
            Assert.Contains(new AssistantText("host said allow to r1"), seen);
            Assert.IsType<AssistantTurnEnded>(seen[^1]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
