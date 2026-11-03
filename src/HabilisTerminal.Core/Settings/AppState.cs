using System.Text.Json;
using System.Text.Json.Serialization;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Panels;

namespace HabilisTerminal.Core.Settings;

/// <summary>One panel as it was left (SPEC §14).</summary>
public sealed record PanelSnapshot
{
    public string? Path { get; init; }

    public PanelViewMode ViewMode { get; init; } = PanelViewMode.Medium;

    public SortMode Sort { get; init; } = SortMode.Name;

    public bool SortDescending { get; init; }

    public bool SelectedFirst { get; init; }

    public bool? ShowHidden { get; init; }

    public bool Visible { get; init; } = true;
}

/// <summary>Restored (not maximised) window bounds in physical pixels, workspace coordinates (<c>GetWindowPlacement</c>).</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized)
{
    private const int SwShowMaximized = 3;
    private const int SwShowMinimized = 2;
    private const int WpfRestoreToMaximized = 0x2;

    /// <summary>
    /// Maps <c>WINDOWPLACEMENT.showCmd</c>/<c>flags</c> to "reopen maximized": a window minimized from maximized
    /// (<c>WPF_RESTORETOMAXIMIZED</c>) counts as maximized, one minimized from normal as normal.
    /// </summary>
    public static bool IsMaximized(int showCmd, int flags) =>
        showCmd == SwShowMaximized || (showCmd == SwShowMinimized && (flags & WpfRestoreToMaximized) != 0);
}

/// <summary>A file opened in the viewer or editor, for Alt+F11.</summary>
public sealed record FileHistoryEntry(string Path, bool Edited);

/// <summary>Everything restored on the next start, kept in state.json (not settings: the user never edits it).</summary>
public sealed record AppState
{
    public PanelSnapshot Left { get; init; } = new();

    public PanelSnapshot Right { get; init; } = new();

    /// <summary>"Left" or "Right".</summary>
    public string ActivePanel { get; init; } = "Left";

    public int? TerminalRows { get; init; }

    public double? SplitRatio { get; init; }

    public double? SinglePanelRatio { get; init; }

    public WindowPlacement? Window { get; init; }

    public IReadOnlyList<string> FolderHistory { get; init; } = [];

    public IReadOnlyList<FileHistoryEntry> FileHistory { get; init; } = [];

    /// <summary>Hotkeys of plugin items in the drive menu ("pluginId/title" → key, issue #66).</summary>
    public IReadOnlyDictionary<string, string> PluginHotkeys { get; init; } = new Dictionary<string, string>();
}

/// <summary>Reads and writes state.json atomically; a missing or broken file gives the defaults.</summary>
public sealed class StateStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string FilePath { get; } = System.IO.Path.GetFullPath(path);

    public AppState Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath), Options) ?? new AppState() : new AppState();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new AppState();
        }
    }

    public void Save(AppState state) => SettingsStore.WriteAtomically(FilePath, JsonSerializer.Serialize(state, Options));
}
