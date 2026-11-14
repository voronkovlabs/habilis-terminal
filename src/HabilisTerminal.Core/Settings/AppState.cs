using System.Text.Json;
using System.Text.Json.Serialization;
using HabilisTerminal.Core.Diagnostics;
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

    /// <summary>The item under the cursor (issue #125).</summary>
    public string? CurrentName { get; init; }
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

    /// <summary>The interface zoom, 1 for 100% (issue #237).</summary>
    public double? Zoom { get; init; }

    public IReadOnlyList<string> FolderHistory { get; init; } = [];

    public IReadOnlyList<FileHistoryEntry> FileHistory { get; init; } = [];

    /// <summary>The command line's recent commands, most recent first, by id (issue #155).</summary>
    public IReadOnlyList<string> RecentCommands { get; init; } = [];

    /// <summary>An update the user chose to skip (issue #298): the background check does not offer it again.</summary>
    public string? SkippedUpdate { get; init; }

    /// <summary>Hotkeys of plugin items in the drive menu ("pluginId/title" → key, issue #66).</summary>
    public IReadOnlyDictionary<string, string> PluginHotkeys { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Reads and writes state.json atomically; a missing or broken file gives the defaults. Writing can wait for the disk for
/// a long time, so the periodic save runs in the background (issue #136). The newest state asked for wins, in any order.
/// </summary>
public sealed class StateStore(string path)
{
    private readonly Lock _gate = new();
    private long _lastTicket;
    private long _writtenTicket;
    private string? _writtenText;

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

    /// <summary>Writes now, on this thread: for when the app is about to exit.</summary>
    public void Save(AppState state) => Write(state, Interlocked.Increment(ref _lastTicket));

    /// <summary>Writes on another thread. <paramref name="state"/> must not change afterwards.</summary>
    public Task SaveInBackground(AppState state, Action<Exception>? failed = null)
    {
        var ticket = Interlocked.Increment(ref _lastTicket);
        return Task.Run(() =>
        {
            try
            {
                Write(state, ticket);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed?.Invoke(ex);
            }
        });
    }

    private void Write(AppState state, long ticket)
    {
        using var measure = Perf.Measure("state.save");
        lock (_gate)
        {
            if (ticket < _writtenTicket)
            {
                return; // a newer state is already on the disk
            }

            // Most minutes nothing changed: the disk is left alone then.
            var text = JsonSerializer.Serialize(state, Options);
            if (text != _writtenText || !File.Exists(FilePath))
            {
                SettingsStore.WriteAtomically(FilePath, text);
                _writtenText = text;
            }

            _writtenTicket = ticket;
        }
    }
}
