using System.Text.Json;

namespace HabilisTerminal.Core.Control;

/// <summary>One row of a panel, as the control API reports it (issue #387).</summary>
public sealed record ControlRow(string Name, bool Folder, long? Size, DateTime? Modified, bool Marked);

/// <summary>A panel: where it is, what is under the cursor, what is marked, and its rows (the first ones only).</summary>
public sealed record ControlPanel(
    string Side,
    bool Active,
    bool Visible,
    string Location,
    string? Plugin,
    string? Title,
    string? Message,
    string? Cursor,
    IReadOnlyList<string> Marked,
    int Count,
    IReadOnlyList<ControlRow> Rows);

/// <summary>The whole window: both panels, the page open over them, the console.</summary>
public sealed record ControlState(
    string ActivePanel,
    ControlPanel Left,
    ControlPanel Right,
    string? Page,
    string ConsoleFolder,
    string ShellState,
    int ConsoleTabs);

/// <summary>What a console command printed and how it ended.</summary>
public sealed record ControlConsoleResult(bool Finished, int? ExitCode, string Output);

/// <summary>The window as a PNG, base64.</summary>
public sealed record ControlScreenshot(int Width, int Height, string Png);

/// <summary>A setting and its value as the settings file holds it.</summary>
public sealed record ControlSetting(string Key, string Title, string Kind, JsonElement Value, JsonElement Default);

/// <summary>
/// How far the control API may go (setting <c>api.control</c>): off, ask before a console command or a settings change,
/// or trusted. Panel moves and catalog commands never ask: the app's own confirmations (delete, overwrite) still show.
/// </summary>
public enum ControlMode
{
    Off,
    Ask,
    Trusted,
}

/// <summary>What the control API asks of the app (issue #387). The app runs these on its UI thread.</summary>
public interface IControlApiHandler
{
    ControlMode Mode { get; }

    Task<ControlState> StateAsync(int maxRows);

    Task<ControlPanel> PanelAsync(string? side, int maxRows);

    Task<ControlPanel> NavigateAsync(string? side, string target, int maxRows);

    Task<ControlPanel> ActivateAsync(string side, int maxRows);

    Task<ControlPanel> CursorAsync(string? side, string name, int maxRows);

    /// <param name="mode">"set" (only these), "add", "remove" or "clear".</param>
    Task<ControlPanel> SelectAsync(string? side, IReadOnlyList<string> names, string mode, int maxRows);

    /// <summary>A command of the catalog (Ctrl+Shift+P), as if the user chose it.</summary>
    Task RunCommandAsync(string id);

    /// <summary>Types a command line into the console, runs it and waits for the prompt (or the timeout).</summary>
    Task<ControlConsoleResult> ConsoleRunAsync(string command, int timeoutMs);

    /// <summary>The console's last lines, the screen and the scrollback above it.</summary>
    Task<string> ConsoleReadAsync(int lines);

    Task<ControlState> OpenFileAsync(string path, bool edit);

    Task<ControlState> ClosePageAsync();

    Task<ControlScreenshot> ScreenshotAsync();

    Task<IReadOnlyList<ControlSetting>> SettingsAsync(string? key);

    Task<ControlSetting> SetSettingAsync(string key, JsonElement value);
}
