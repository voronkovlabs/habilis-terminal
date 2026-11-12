using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HabilisTerminal.App.Theming;
using HabilisTerminal.Core.Panels;
using HabilisTerminal.Themes;

namespace HabilisTerminal.App.Panels;

/// <summary>
/// File icons for the panels (SPEC §10): VS Code icon theme vectors (Material by default, issue #50; Seti on request), a generic
/// folder when the theme has none, and the system's own icons for <c>.exe</c>, <c>.ico</c> and <c>.lnk</c>, loaded
/// lazily for visible rows only.
/// </summary>
internal static class IconProvider
{
    public const double Size = 16;

    private static readonly HashSet<string> SystemIconExtensions = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".ico", ".lnk", ".url" };
    private static readonly Geometry FolderGeometry = Freeze(Geometry.Parse("M1,3 L6,3 L7.5,4.5 L15,4.5 L15,13 L1,13 Z"));
    private static readonly Geometry ParentGeometry = Freeze(Geometry.Parse("M8,3 L3,8 M8,3 L13,8 M8,3 L8,13"));
    private static readonly Dictionary<(string Id, bool Light), Drawing> ThemeDrawings = [];
    private static readonly Dictionary<string, Bitmap?> SystemIcons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim SystemIconLoadGate = new(1, 1);
    private static FileIconTheme _theme = LoadTheme();

    /// <summary>A system icon finished loading: repaint the panels.</summary>
    public static event Action? SystemIconLoaded;

    public static string ThemeId { get; private set; } = FileIconTheme.Material;

    /// <summary>Switches the icon theme (Seti / Material); the caller repaints. Returns false when nothing changed.</summary>
    public static bool SetTheme(string id)
    {
        if (string.Equals(id, ThemeId, StringComparison.OrdinalIgnoreCase) || !FileIconTheme.BuiltInThemes.Contains(id))
        {
            return false;
        }

        _theme = FileIconTheme.LoadBuiltIn(id);
        ThemeId = id;
        ThemeDrawings.Clear();
        return true;
    }

    /// <summary>Colours depend on light / dark: drop cached drawings after a colour theme change.</summary>
    public static void OnColorThemeChanged() => ThemeDrawings.Clear();

    public static void Draw(DrawingContext dc, PanelItem item, string directory, double x, double y)
    {
        if (item.IsParent)
        {
            DrawOutline(dc, ParentGeometry, PanelPalette.FolderIcon, x, y);
            return;
        }

        var entry = item.Entry;
        if (!entry.IsDirectory && SystemIconExtensions.Contains(Path.GetExtension(entry.Name)) && SystemIcon(Path.Combine(directory, entry.Name)) is { } image)
        {
            dc.DrawImage(image, new Rect(x, y, Size, Size));
            return;
        }

        var definition = entry.IsDirectory ? _theme.ResolveFolder(entry.Name, PanelPalette.IsLight) : _theme.ResolveFile(entry.Name, PanelPalette.IsLight);
        if (definition is null)
        {
            // Only folders can be missing (Seti has no folder icons).
            DrawOutline(dc, FolderGeometry, PanelPalette.FolderIcon, x, y);
            return;
        }

        using (dc.PushTransform(Matrix.CreateTranslation(x, y)))
        {
            ThemeDrawing(definition).Draw(dc);
        }
    }

    /// <summary>Draws a named icon for plugin rows (<c>PanelIcons</c>, issue #69); false for an unknown name.</summary>
    public static bool DrawNamed(DrawingContext dc, string name, double x, double y)
    {
        if (NamedIcons.Get(name) is not { } drawing)
        {
            return false;
        }

        using (dc.PushTransform(Matrix.CreateTranslation(x, y)))
        {
            drawing.Draw(dc);
        }

        return true;
    }

    /// <summary>Draws the system's icon of an absolute local path, loading it lazily for visible rows.</summary>
    public static bool DrawSystem(DrawingContext dc, string path, double x, double y)
    {
        if (!Path.IsPathFullyQualified(path) || SystemIcon(path) is not { } image)
        {
            return false;
        }

        dc.DrawImage(image, new Rect(x, y, Size, Size));
        return true;
    }

    private static FileIconTheme LoadTheme() => FileIconTheme.LoadBuiltIn(FileIconTheme.Material);

    private static Drawing ThemeDrawing(IconDefinition definition)
    {
        var key = (definition.Id, PanelPalette.IsLight);
        if (ThemeDrawings.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var group = new DrawingGroup();
        foreach (var shape in definition.Shapes)
        {
            var brush = new SolidColorBrush(PanelPalette.ToColor(shape.Fill));
            group.Children.Add(new GeometryDrawing { Brush = brush, Geometry = Geometry.Parse(shape.Data) });
        }

        // View box → 16×16 cell.
        var scale = Size / Math.Max(definition.Width, definition.Height);
        var transform = new TransformGroup();
        transform.Children.Add(new TranslateTransform(-definition.X, -definition.Y));
        transform.Children.Add(new ScaleTransform(scale, scale));
        group.Transform = transform;
        group.ClipGeometry = new RectangleGeometry(new Rect(definition.X, definition.Y, definition.Width, definition.Height));
        ThemeDrawings[key] = group;
        return group;
    }

    private static void DrawOutline(DrawingContext dc, Geometry geometry, IBrush brush, double x, double y)
    {
        using (dc.PushTransform(Matrix.CreateTranslation(x, y)))
        {
            dc.DrawGeometry(null, new Pen(brush, 1.2), geometry);
        }
    }

    private static Bitmap? SystemIcon(string path)
    {
        if (SystemIcons.TryGetValue(path, out var icon))
        {
            return icon;
        }

        if (Pending.Add(path))
        {
            // Shell icon handlers can load packages, libraries and antivirus filters. Reports #192 and #193 caught
            // SHGetFileInfo blocking the UI thread for seconds, so extraction happens away from the Dispatcher.
            _ = LoadSystemIconAsync(path, Avalonia.Threading.Dispatcher.UIThread);
        }

        return null;
    }

    private static async Task LoadSystemIconAsync(string path, Dispatcher dispatcher)
    {
        Bitmap? icon = null;
        try
        {
            icon = await Task.Run(async () =>
            {
                await SystemIconLoadGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    return LoadSystemIcon(path);
                }
                finally
                {
                    SystemIconLoadGate.Release();
                }
            }).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExternalException or ArgumentException)
        {
            // A shell extension or protected file may reject the request; cache the fallback just as before.
        }

        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                Pending.Remove(path);
                SystemIcons[path] = icon;
                SystemIconLoaded?.Invoke();
            }, DispatcherPriority.Background);
        }
        catch (TaskCanceledException)
        {
            // The window is closing; there is nobody left to repaint.
        }
        catch (InvalidOperationException)
        {
            // The Dispatcher has already shut down.
        }
    }

    private static Bitmap? LoadSystemIcon(string path)
    {
        using var measure = Core.Diagnostics.Perf.Enabled ? Core.Diagnostics.Perf.Measure("icon.system", Reporting.PerfDiagnostics.ExtensionKind(path)) : default;
        return Platform.Desktop.Current.FileIcon(path, (int)Size);
    }

    private static Geometry Freeze(Geometry geometry)
    {
        return geometry;
    }
}
