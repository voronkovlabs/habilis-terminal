using System.IO;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Control;
using HabilisTerminal.Core.Plugins;
using HabilisTerminal.Core.Settings;
using HabilisTerminal.Shell.Integration;

namespace HabilisTerminal.App;

/// <summary>
/// Claude control (issue #387): the app side of the control methods of the local API, which <c>habi mcp</c> serves to
/// Claude as MCP tools. Everything runs on the UI thread, as the user's own keys would.
/// </summary>
public partial class MainWindow : IControlApiHandler
{
    private readonly TextBlock _controlMark = new()
    {
        Text = "● Claude",
        IsVisible = false,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        Margin = new Thickness(12, 0, 0, 0),
        FontSize = 12,
    };

    private DateTime _controlSeen;
    private Avalonia.Threading.DispatcherTimer? _controlTimer;

    ControlMode IControlApiHandler.Mode => _settings.ApiControl switch
    {
        "ask" => ControlMode.Ask,
        "trusted" => ControlMode.Trusted,
        _ => ControlMode.Off,
    };

    /// <summary>The title bar says Claude is in control while calls come, and every call goes to control.log.</summary>
    private void OnControlCalled(string method, string parameters)
    {
        try
        {
            var log = Path.Combine(App.Paths.SettingsDirectory, "control.log");
            File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {method} {parameters}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The journal is a help, not a condition.
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _controlSeen = DateTime.UtcNow;
            if (_controlMark.Parent is null)
            {
                _controlMark.Foreground = Theming.PanelPalette.MarkedForeground;
                Avalonia.Controls.ToolTip.SetTip(_controlMark, "Claude is driving this window (Settings, Claude Control). Calls are logged in control.log.");
                _titleBar.MenuStrip.Children.Add(_controlMark);
            }

            _controlMark.IsVisible = true;
            _controlTimer ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(2), Avalonia.Threading.DispatcherPriority.Background, (_, _) =>
            {
                if (DateTime.UtcNow - _controlSeen > TimeSpan.FromSeconds(15))
                {
                    _controlMark.IsVisible = false;
                    _controlTimer?.Stop();
                }
            });
            _controlTimer.Start();
        });
    }

    Task<ControlState> IControlApiHandler.StateAsync(int maxRows) => OnUiAsync(() => Task.FromResult(ControlStateNow(maxRows)));

    Task<ControlPanel> IControlApiHandler.PanelAsync(string? side, int maxRows) => OnUiAsync(() => Task.FromResult(Describe(PanelFor(side), maxRows)));

    Task<ControlPanel> IControlApiHandler.NavigateAsync(string? side, string target, int maxRows) => OnUiAsync(async () =>
    {
        var panel = PanelFor(side);
        LeaveOtherContent(panel);
        if (!IsShown(panel))
        {
            SetPanelVisibility(panel == _left || Workspace.LeftVisible, panel == _right || Workspace.RightVisible);
        }

        var opened = await OpenControlTargetAsync(panel, target);
        if (panel == _active)
        {
            SyncShellDirectory();
        }

        return opened ? Describe(panel, maxRows) : throw new PluginApiException(PluginApiException.NotFound, panel.Message ?? $"Nothing opened \"{target}\".");
    });

    Task<ControlPanel> IControlApiHandler.ActivateAsync(string side, int maxRows) => OnUiAsync(() =>
    {
        var panel = PanelFor(side);
        if (!IsShown(panel))
        {
            SetPanelVisibility(panel == _left || Workspace.LeftVisible, panel == _right || Workspace.RightVisible);
        }

        Activate(panel);
        SyncShellDirectory();
        return Task.FromResult(Describe(panel, maxRows));
    });

    Task<ControlPanel> IControlApiHandler.CursorAsync(string? side, string name, int maxRows) => OnUiAsync(() =>
    {
        var panel = PanelFor(side);
        if (!panel.State.TrySetCursor(name))
        {
            throw new PluginApiException(PluginApiException.NotFound, $"There is no \"{name}\" in {panel.CurrentPath}.");
        }

        panel.Refresh();
        return Task.FromResult(Describe(panel, maxRows));
    });

    Task<ControlPanel> IControlApiHandler.SelectAsync(string? side, IReadOnlyList<string> names, string mode, int maxRows) => OnUiAsync(() =>
    {
        var panel = PanelFor(side);
        var selection = panel.State.Selection;
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var entries = panel.State.Entries;
        var missing = wanted.Where(n => !entries.Any(e => string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0 && mode != "clear")
        {
            throw new PluginApiException(PluginApiException.NotFound, $"Not in {panel.CurrentPath}: {string.Join(", ", missing)}.");
        }

        switch (mode)
        {
            case "set":
                selection.Clear();
                goto case "add";
            case "add":
                foreach (var entry in entries.Where(e => wanted.Contains(e.Name)))
                {
                    selection.Set(entry, true);
                }

                break;
            case "remove":
                foreach (var entry in entries.Where(e => wanted.Contains(e.Name)))
                {
                    selection.Set(entry, false);
                }

                break;
            case "clear":
                selection.Clear();
                break;
            default:
                throw new PluginApiException(PluginApiException.BadRequest, "params.mode is set, add, remove or clear.");
        }

        panel.Refresh();
        return Task.FromResult(Describe(panel, maxRows));
    });

    Task IControlApiHandler.RunCommandAsync(string id) => OnUiAsync(() =>
    {
        var context = CaptureCommandContext();
        var command = AllCommands().FirstOrDefault(c => c.Id == id)
            ?? throw new PluginApiException(PluginApiException.NotFound, $"There is no command \"{id}\". list_commands shows them.");
        if (!command.AppliesWhen(context))
        {
            throw new PluginApiException(PluginApiException.NotFound, $"\"{id}\" does not apply now (list_commands shows what does).");
        }

        RememberCommand(command.Id);
        command.Run?.Invoke();
        return Task.FromResult(true);
    });

    async Task<ControlConsoleResult> IControlApiHandler.ConsoleRunAsync(string command, int timeoutMs, bool ask)
    {
        var (host, emulator, start) = await OnUiAsync(() =>
        {
            if (_tab is not { } tab)
            {
                throw new PluginApiException(PluginApiException.NotFound, "There is no console.");
            }

            if (tab.Host.State != ShellState.Idle)
            {
                throw new PluginApiException(PluginApiException.Busy, "The console is running a command. Wait for it, or read it with read_console.");
            }

            if (ask && !Confirm("Claude wants to run a command", command))
            {
                throw new PluginApiException(PluginApiException.Declined, "The user declined the command.");
            }

            var emulator = tab.Terminal.Emulator;
            long start;
            lock (emulator.SyncRoot)
            {
                start = emulator.DroppedLines + emulator.ScrollbackCount + emulator.CursorY;
            }

            return Task.FromResult((tab.Host, emulator, start));
        });

        var finished = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished(int? code) => finished.TrySetResult(code);
        host.CommandFinished += OnFinished;
        try
        {
            await OnUiAsync(() =>
            {
                host.Run(command);
                return Task.FromResult(true);
            });
            var done = await Task.WhenAny(finished.Task, Task.Delay(Math.Clamp(timeoutMs, 100, 3_600_000))) == finished.Task;
            if (done)
            {
                // The prompt after the output comes right after the end mark.
                await WaitForAsync(() => host.State == ShellState.Idle, TimeSpan.FromSeconds(3));
            }

            return new ControlConsoleResult(done, done ? finished.Task.Result : null, emulator.OutputAfter(start));
        }
        finally
        {
            host.CommandFinished -= OnFinished;
        }
    }

    Task<string> IControlApiHandler.ConsoleReadAsync(int lines) => OnUiAsync(() =>
    {
        var emulator = _tab?.Terminal.Emulator ?? throw new PluginApiException(PluginApiException.NotFound, "There is no console.");
        lock (emulator.SyncRoot)
        {
            var last = emulator.LastTextLine();
            var first = Math.Max(0, last - Math.Clamp(lines, 1, 10_000) + 1);
            return Task.FromResult(emulator.TextBetween(first, 0, last, int.MaxValue).TrimEnd());
        }
    });

    Task<ControlState> IControlApiHandler.OpenFileAsync(string path, bool edit) => OnUiAsync(() =>
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new PluginApiException(PluginApiException.NotFound, $"There is no file {full}.");
        }

        if (edit)
        {
            Edit(full, _active);
        }
        else
        {
            OpenViewer(full, _active);
        }

        return Task.FromResult(ControlStateNow(0));
    });

    Task<ControlState> IControlApiHandler.ClosePageAsync() => OnUiAsync(() =>
    {
        if (IsPageOpen)
        {
            ClosePage();
        }

        return Task.FromResult(ControlStateNow(0));
    });

    Task<ControlScreenshot> IControlApiHandler.ScreenshotAsync() => OnUiAsync(() =>
    {
        var scale = RenderScaling;
        var size = new PixelSize(Math.Max(1, (int)(Bounds.Width * scale)), Math.Max(1, (int)(Bounds.Height * scale)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(this);
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return Task.FromResult(new ControlScreenshot(size.Width, size.Height, Convert.ToBase64String(stream.ToArray())));
    });

    Task<IReadOnlyList<ControlSetting>> IControlApiHandler.SettingsAsync(string? key) => OnUiAsync(() =>
    {
        var definitions = key is null ? SettingsCatalog.All : [SettingsCatalog.Find(key) ?? throw new PluginApiException(PluginApiException.NotFound, $"There is no setting \"{key}\".")];
        return Task.FromResult<IReadOnlyList<ControlSetting>>([.. definitions.Select(DescribeSetting)]);
    });

    Task<ControlSetting> IControlApiHandler.SetSettingAsync(string key, JsonElement value, bool ask) => OnUiAsync(() =>
    {
        var definition = SettingsCatalog.Find(key) ?? throw new PluginApiException(PluginApiException.NotFound, $"There is no setting \"{key}\".");
        var (converted, error) = definition.Convert(value);
        if (error is not null)
        {
            throw new PluginApiException(PluginApiException.BadRequest, error);
        }

        // Claude cannot widen its own rights.
        if (key == "api.control")
        {
            throw new PluginApiException(PluginApiException.Declined, "Claude Control can only be changed by the user.");
        }

        if (ask && !Confirm("Claude wants to change a setting", $"{definition.Title} ({key}): {value.GetRawText()}"))
        {
            throw new PluginApiException(PluginApiException.Declined, "The user declined the change.");
        }

        ChangeSetting(key, converted);
        return Task.FromResult(DescribeSetting(definition));
    });

    private async Task<bool> OpenControlTargetAsync(FilePanel panel, string target)
    {
        var clean = Core.Panels.TypedLocation.Clean(target);
        if (Core.Panels.TypedLocation.IsPath(clean))
        {
            var path = Path.GetFullPath(Core.Panels.TypedLocation.ToSystemPath(clean));
            if (File.Exists(path) && Path.GetDirectoryName(path) is { } folder)
            {
                return await panel.NavigateAsync(folder, Path.GetFileName(path));
            }
        }

        return await NavigateOrOpenAsync(panel, clean);
    }

    private FilePanel PanelFor(string? side) => side?.ToLowerInvariant() switch
    {
        null => _active,
        "left" => _left,
        "right" => _right,
        _ => throw new PluginApiException(PluginApiException.BadRequest, "params.side is left or right."),
    };

    private ControlState ControlStateNow(int maxRows) => new(
        _active.Side.ToLowerInvariant(),
        Describe(_left, maxRows),
        Describe(_right, maxRows),
        PageHost.Content switch
        {
            Viewing.ViewerPage viewer => $"viewer: {viewer.FilePath}",
            Editing.EditorPage => "editor",
            SettingsUi.SettingsPage => "settings",
            null => null,
            var other => other.GetType().Name,
        },
        _tab?.Host.CurrentDirectory ?? string.Empty,
        _tab?.Host.State.ToString() ?? "none",
        _tabs.Count);

    private ControlPanel Describe(FilePanel panel, int maxRows)
    {
        var state = panel.State;
        var rows = Enumerable.Range(0, state.Count).Select(state.ItemAt).Where(i => !i.IsParent).ToList();
        return new ControlPanel(
            panel.Side.ToLowerInvariant(),
            panel == _active,
            IsShown(panel),
            panel.Provider?.Location ?? panel.CurrentPath,
            panel.Plugin?.Id,
            panel.Provider?.Title,
            panel.Message,
            state.Count > 0 && !state.Current.IsParent ? state.Current.Name : null,
            [.. state.Selection.Names],
            rows.Count,
            [.. rows.Take(Math.Max(0, maxRows)).Select(i => new ControlRow(
                i.Name,
                i.Entry.IsDirectory,
                i.Entry.IsDirectory ? null : i.Entry.Length,
                i.Entry.LastWriteTimeUtc == default ? null : i.Entry.LastWriteTimeUtc,
                state.Selection.Contains(i.Entry)))]);
    }

    private ControlSetting DescribeSetting(SettingDefinition definition) => new(
        definition.Key,
        definition.Title,
        definition.Kind.ToString().ToLowerInvariant(),
        JsonSerializer.SerializeToElement(_settings.GetValue(definition.Key)),
        JsonSerializer.SerializeToElement(definition.Default));

    private bool Confirm(string title, string what)
    {
        var body = new TextBlock { Text = what, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
        body.Classes.Add("label");
        return ThemedDialog.Show(this, title, body,
            [new DialogButton<bool>("_Allow", true), new DialogButton<bool>("Cancel", false, IsCancel: true, IsDefault: true)], false);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(50);
        }
    }
}
