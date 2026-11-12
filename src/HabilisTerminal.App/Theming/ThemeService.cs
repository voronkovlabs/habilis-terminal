using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using HabilisTerminal.Themes;
using Avalonia.Media;
using Avalonia.Threading;
using HabilisTerminal.App.Hosting;

namespace HabilisTerminal.App.Theming;

/// <summary>
/// Chooses the theme (SPEC §10): "auto" picks the built-in Dark / Light Modern (or high contrast) by the Windows
/// mode; otherwise a built-in theme name, a VS Code theme file in the themes folder, or a path. Tinted with the Windows
/// accent colour unless turned off; reloads when Windows settings or the theme file change.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private FileSystemWatcher? _fileWatcher;
    private Avalonia.Threading.DispatcherTimer? _fileReload;
    private string _requested = "auto";
    private bool _useAccent = true;

    public event Action<ColorTheme>? Changed;

    public ColorTheme Current { get; private set; } = ThemeLoader.LoadBuiltIn(ThemeLoader.DarkModern);

    public string? LastError { get; private set; }

    /// <summary>Folder searched for user theme files.</summary>
    public string? ThemesDirectory { get; set; }

    public void Dispose() => _fileWatcher?.Dispose();

    /// <summary>Applies the <c>theme</c> and <c>useAccentColor</c> settings; reloads only when they changed.</summary>
    public void Configure(string requested, bool useAccent, bool force = false)
    {
        if (!force && requested == _requested && useAccent == _useAccent)
        {
            return;
        }

        _requested = requested;
        _useAccent = useAccent;
        Reload();
    }

    public void Reload()
    {
        LastError = null;
        ColorTheme theme;
        var file = ThemeFile(_requested);
        try
        {
            theme = file is not null ? ThemeLoader.LoadFile(file)
                : BuiltInName(_requested) is { } builtIn ? ThemeLoader.LoadBuiltIn(builtIn)
                : string.IsNullOrWhiteSpace(_requested) || _requested.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? ThemeLoader.LoadBuiltIn(SystemAppearance.BuiltInThemeFor(SystemAppearance.IsHighContrast()))
                    : throw new ThemeLoadException($"Theme '{_requested}' was not found.");
        }
        catch (ThemeLoadException ex)
        {
            // Invalid theme: fall back to the built-in one and report it.
            LastError = ex.Message;
            theme = ThemeLoader.LoadBuiltIn(SystemAppearance.BuiltInThemeFor(SystemAppearance.IsHighContrast()));
        }

        var keepsOwnAccent = file is null && BuiltInName(_requested) is { } fixedPalette && ThemeLoader.KeepsOwnAccent(fixedPalette);
        if (_useAccent && !keepsOwnAccent && SystemAppearance.AccentColor is { } accent)
        {
            theme = SystemAppearance.ApplyAccent(theme, accent);
        }

        Current = theme;
        PanelPalette.Apply(theme);
        WatchThemeFile(file);
        Changed?.Invoke(theme);
    }

    private static string? BuiltInName(string requested)
    {
        var name = requested.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? requested : requested + ".json";
        return ThemeLoader.BuiltInThemes.FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
    }

    private string? ThemeFile(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || requested.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Path.IsPathRooted(requested))
        {
            return File.Exists(requested) ? requested : null;
        }

        if (ThemesDirectory is { } directory)
        {
            foreach (var candidate in new[] { requested, requested + ".json" })
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>Theme names offered by the Settings page: "auto", the built-in themes and files in the themes folder.</summary>
    public IReadOnlyList<string> AvailableThemes()
    {
        var names = new List<string> { "auto" };
        names.AddRange(ThemeLoader.BuiltInThemes.Select(t => Path.GetFileNameWithoutExtension(t)));
        if (ThemesDirectory is { } directory && Directory.Exists(directory))
        {
            names.AddRange(Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>());
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

    private void ScheduleFileReload() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        // Editors save in several writes: wait until the file is quiet.
        _fileReload ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _fileReload!.Stop();
            Reload();
        });
        _fileReload.Stop();
        _fileReload.Start();
    });

    /// <summary>The console's colours from the <c>terminal.*</c> keys.</summary>
    public static TerminalPalette ToTerminalPalette(ColorTheme theme)
    {
        var background = theme.GetOpaque("terminal.background");
        Color Opaque(string key, ThemeColor? fallback = null)
        {
            var color = (theme.Get(key) ?? fallback ?? background).Over(background);
            return Color.FromRgb(color.R, color.G, color.B);
        }

        var foreground = Opaque("terminal.foreground");
        var selection = theme.Get("terminal.selectionBackground") is { } s ? Color.FromArgb(s.A == 255 ? (byte)0x80 : s.A, s.R, s.G, s.B) : Color.FromArgb(0x80, 0x26, 0x4F, 0x78);
        var cursor = theme.Get("terminalCursor.foreground") is { } c ? Color.FromRgb(c.R, c.G, c.B) : foreground;
        return new TerminalPalette(foreground, Opaque("terminal.background"), selection, cursor,
            ThemeDefaults.TerminalAnsiKeys.Select(key => Opaque(key)).ToList());
    }
}
