using System.Diagnostics;
using System.Text.Json.Nodes;

namespace HabilisTerminal.Core.Assistant;

/// <summary>Who answers: whatever Claude Code itself is set up with (a subscription or an API key), or Amazon Bedrock.</summary>
public enum AssistantProvider
{
    ClaudeCode,
    Bedrock,
}

/// <summary>How to start Claude Code for the assistant (issue #197).</summary>
/// <param name="Claude">The claude executable, or null to look for it on PATH and in its usual install folders.</param>
/// <param name="WorkingDirectory">Where the session works: the active panel's folder.</param>
/// <param name="Habi">The app's own executable: Claude Code starts <c>habi mcp</c> from it to drive the window.</param>
/// <param name="AppProcessId">The window the session drives (HABI_PID).</param>
/// <param name="Token">Proves to the app that a control call comes from this session.</param>
public sealed record ClaudeLaunchOptions(
    string? Claude,
    string WorkingDirectory,
    string Habi,
    int AppProcessId,
    string Token,
    AssistantProvider Provider = AssistantProvider.ClaudeCode,
    string? Model = null,
    string? AwsProfile = null,
    string? AwsRegion = null);

/// <summary>The command line and environment that start a Claude Code session for the assistant.</summary>
public static class ClaudeLaunch
{
    /// <summary>Habilis tools that only look: they run without asking.</summary>
    public static readonly string[] ReadOnlyTools =
    [
        "Read", "Glob", "Grep",
        "mcp__habilis__get_state", "mcp__habilis__get_panel", "mcp__habilis__read_console", "mcp__habilis__list_commands",
        "mcp__habilis__screenshot", "mcp__habilis__list_apps", "mcp__habilis__get_settings",
    ];

    /// <summary>What the assistant is told about where it is, after Claude Code's own instructions.</summary>
    public const string SystemPrompt =
        """
        You are the assistant built into Habilis Terminal, a two-panel file manager for Windows (Far Manager keys) with a PowerShell console.
        The user talks to you from the app's assistant page; the window is behind it.
        The habilis MCP tools see and drive that window: both panels, marks, the command list and the console. get_state shows where the user is; start there when the request is about "this folder" or "these files".
        Your working directory is the active panel's folder when the session started.
        Deleting: move files to the Recycle Bin rather than deleting them for good, unless the user asks for that. In PowerShell: Add-Type -AssemblyName Microsoft.VisualBasic; [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($path, 'OnlyErrorDialogs', 'SendToRecycleBin') (DeleteDirectory for folders).
        Before deleting or moving many files, say what you are going to do and how many files it affects.
        Answer in the user's language.
        """;

    /// <summary>The process to start, and the MCP configuration file it reads (the caller deletes it when the session ends).</summary>
    public static (ProcessStartInfo Start, string McpConfig) Build(ClaudeLaunchOptions options)
    {
        var claude = options.Claude is { Length: > 0 } given ? given : Find() ?? throw new FileNotFoundException(
            "Claude Code is not installed, or not on PATH. Install it (https://claude.com/claude-code) or set assistant.claudePath.");

        var mcpConfig = Path.Combine(Path.GetTempPath(), $"habi-assistant-{options.AppProcessId}-{Guid.NewGuid():N}.json");
        File.WriteAllText(mcpConfig, McpConfig(options.Habi).ToJsonString());

        var start = new ProcessStartInfo
        {
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
        };

        // An npm install is a .cmd script, which only cmd starts.
        if (claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || claude.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(claude);
        }
        else
        {
            start.FileName = claude;
        }

        foreach (var argument in Arguments(options, mcpConfig))
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["HABI_PID"] = options.AppProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["HABI_ASSISTANT_TOKEN"] = options.Token;
        if (options.Provider == AssistantProvider.Bedrock)
        {
            start.Environment["CLAUDE_CODE_USE_BEDROCK"] = "1";
            if (options.AwsProfile is { Length: > 0 } profile)
            {
                start.Environment["AWS_PROFILE"] = profile;
            }

            if (options.AwsRegion is { Length: > 0 } region)
            {
                start.Environment["AWS_REGION"] = region;
            }
        }
        else
        {
            // The app's own environment must not switch Claude Code to Bedrock behind the setting's back.
            start.Environment.Remove("CLAUDE_CODE_USE_BEDROCK");
        }

        return (start, mcpConfig);
    }

    /// <summary>The arguments after the executable.</summary>
    public static IReadOnlyList<string> Arguments(ClaudeLaunchOptions options, string mcpConfig)
    {
        var arguments = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--permission-prompts", "host",
            "--mcp-config", mcpConfig,
            "--append-system-prompt", SystemPrompt,
            "--allowedTools", string.Join(",", ReadOnlyTools),
        };
        if (options.Model is { Length: > 0 } model)
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        return arguments;
    }

    /// <summary>The MCP server that lets the session drive the window: <c>habi mcp</c>.</summary>
    public static JsonObject McpConfig(string habi) => new()
    {
        ["mcpServers"] = new JsonObject
        {
            ["habilis"] = new JsonObject { ["command"] = habi, ["args"] = new JsonArray("mcp") },
        },
    };

    /// <summary>claude on PATH, or in the folder its installer uses; null when not found.</summary>
    public static string? Find()
    {
        var names = OperatingSystem.IsWindows() ? (string[])["claude.exe", "claude.cmd"] : ["claude"];
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        foreach (var folder in folders)
        {
            foreach (var name in names)
            {
                try
                {
                    var path = Path.Combine(folder.Trim('"'), name);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
                catch (ArgumentException)
                {
                    // A PATH entry with characters a path cannot hold.
                }
            }
        }

        return null;
    }
}
