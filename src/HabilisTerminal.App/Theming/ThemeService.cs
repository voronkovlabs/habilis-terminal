using System.IO;
using System.Windows;
using HabilisTerminal.Themes;
using Microsoft.Terminal.Wpf;

namespace HabilisTerminal.App.Theming;

/// <summary>
/// Chooses the theme (SPEC §10): the built-in Dark / Light Modern (or high contrast) by the Windows mode, tinted
/// with the Windows accent colour; reloads when Windows settings change.
/// Until settings exist (stage 10), <c>HABI_THEME</c> selects a built-in theme name or a VS Code theme file and
/// <c>HABI_USE_ACCENT=0</c> turns the accent off.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private FileSystemWatcher? _fileWatcher;
    private System.Windows.Threading.DispatcherTimer? _fileReload;

    public event Action<ColorTheme>? Changed;

    public ColorTheme Current { get; private set; } = ThemeLoader.LoadBuiltIn(ThemeLoader.DarkModern);

    public string? LastError { get; private set; }

    public void Dispose() => _fileWatcher?.Dispose();

    public void Reload()
    {
        LastError = null;
        ColorTheme theme;
        var requested = Environment.GetEnvironmentVariable("HABI_THEME");
        try
        {
            theme = string.IsNullOrWhiteSpace(requested)
                ? ThemeLoader.LoadBuiltIn(WindowsAppearance.BuiltInThemeFor(SystemParameters.HighContrast))
                : File.Exists(requested) ? ThemeLoader.LoadFile(requested) : ThemeLoader.LoadBuiltIn(requested);
        }
        catch (ThemeLoadException ex)
        {
            // Invalid theme: fall back to the built-in one and report it.
            LastError = ex.Message;
            theme = ThemeLoader.LoadBuiltIn(WindowsAppearance.BuiltInThemeFor(SystemParameters.HighContrast));
        }

        if (Environment.GetEnvironmentVariable("HABI_USE_ACCENT") != "0" && WindowsAppearance.AccentColor is { } accent)
        {
            theme = WindowsAppearance.ApplyAccent(theme, accent);
        }

        Current = theme;
        PanelPalette.Apply(theme);
        WatchThemeFile(requested);
        Changed?.Invoke(theme);
    }

    /// <summary>A theme file chosen by path is reloaded when it is saved (SPEC §10: live theme changes).</summary>
    private void WatchThemeFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _fileWatcher?.Dispose();
            _fileWatcher = null;
            return;
        }

        var full = Path.GetFullPath(path);
        if (_fileWatcher is not null && string.Equals(Path.Combine(_fileWatcher.Path, _fileWatcher.Filter), full, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _fileWatcher?.Dispose();
        _fileWatcher = new FileSystemWatcher(Path.GetDirectoryName(full)!, Path.GetFileName(full))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _fileWatcher.Changed += (_, _) => ScheduleFileReload();
        _fileWatcher.Renamed += (_, _) => ScheduleFileReload();
        _fileWatcher.EnableRaisingEvents = true;
    }

    private void ScheduleFileReload() => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        // Editors save in several writes: wait until the file is quiet.
        _fileReload ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(250), System.Windows.Threading.DispatcherPriority.Background, (_, _) =>
        {
            _fileReload!.Stop();
            Reload();
        }, Application.Current.Dispatcher);
        _fileReload.Stop();
        _fileReload.Start();
    });

    /// <summary>Terminal colours from <c>terminal.*</c> keys (COLORREF values).</summary>
    public static TerminalTheme ToTerminalTheme(ColorTheme theme)
    {
        var background = theme.GetOpaque("terminal.background");
        uint Ref(string key, ThemeColor? fallback = null)
        {
            var color = (theme.Get(key) ?? fallback ?? background).Over(background);
            return (uint)(color.R | (color.G << 8) | (color.B << 16));
        }

        return new TerminalTheme
        {
            DefaultBackground = Ref("terminal.background"),
            DefaultForeground = Ref("terminal.foreground"),
            DefaultSelectionBackground = Ref("terminal.selectionBackground"),
            CursorStyle = CursorStyle.BlinkingBar,
            ColorTable = ThemeDefaults.TerminalAnsiKeys.Select(key => Ref(key)).ToArray(),
        };
    }
}
