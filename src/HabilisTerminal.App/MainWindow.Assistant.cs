using System.Security.Cryptography;
using HabilisTerminal.App.Assistant;
using HabilisTerminal.Core.Assistant;

namespace HabilisTerminal.App;

/// <summary>The built-in assistant (issue #197): Ctrl+Shift+A shows and hides its page; the session lives on while hidden.</summary>
public partial class MainWindow
{
    private AssistantPage? _assistant;

    private void ToggleAssistant()
    {
        if (_assistant is not null && PageHost.Content == _assistant)
        {
            ClosePage();
            return;
        }

        if (_assistant is null)
        {
            _assistant = new AssistantPage(AssistantOptions, token =>
            {
                if (_pluginApi is { } api)
                {
                    api.AssistantToken = token;
                }
            });
            _assistant.CloseRequested += () => ClosePage();
            _assistant.AttentionNeeded += () =>
            {
                if (PageHost.Content != _assistant && PageHost.Content is not Editing.EditorPage)
                {
                    ShowPage(_assistant);
                }
            };
            Closed += (_, _) => _assistant?.Shutdown();
        }

        ShowPage(_assistant);
    }

    /// <summary>Claude Code for this window: the active panel's folder, the provider from the settings, a fresh secret.</summary>
    private ClaudeLaunchOptions AssistantOptions()
    {
        var folder = !_active.IsPlugin && Directory.Exists(_active.CurrentPath)
            ? _active.CurrentPath
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new ClaudeLaunchOptions(
            Claude: Setting(_settings.AssistantClaudePath),
            WorkingDirectory: folder,
            Habi: Environment.ProcessPath ?? "habi.exe",
            AppProcessId: Environment.ProcessId,
            Token: Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Provider: _settings.AssistantProvider == "bedrock" ? AssistantProvider.Bedrock : AssistantProvider.ClaudeCode,
            Model: Setting(_settings.AssistantModel),
            AwsProfile: Setting(_settings.AssistantAwsProfile),
            AwsRegion: Setting(_settings.AssistantAwsRegion));
    }

    private static string? Setting(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
