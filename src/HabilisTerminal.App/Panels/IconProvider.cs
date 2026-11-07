using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private static readonly Dictionary<string, ImageSource?> SystemIcons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);
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

        dc.PushTransform(new TranslateTransform(x, y));
        dc.DrawDrawing(ThemeDrawing(definition));
        dc.Pop();
    }

    /// <summary>Draws a named icon for plugin rows (<c>PanelIcons</c>, issue #69); false for an unknown name.</summary>
    public static bool DrawNamed(DrawingContext dc, string name, double x, double y)
    {
        if (NamedIcons.Get(name) is not { } drawing)
        {
            return false;
        }

        dc.PushTransform(new TranslateTransform(x, y));
        dc.DrawDrawing(drawing);
        dc.Pop();
        return true;
    }

    /// <summary>Draws the Windows shell icon of an absolute local path, loading it lazily for visible rows.</summary>
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
            brush.Freeze();
            group.Children.Add(new GeometryDrawing(brush, null, Geometry.Parse(shape.Data)));
        }

        // View box → 16×16 cell.
        var scale = Size / Math.Max(definition.Width, definition.Height);
        var transform = new TransformGroup();
        transform.Children.Add(new TranslateTransform(-definition.X, -definition.Y));
        transform.Children.Add(new ScaleTransform(scale, scale));
        group.Transform = transform;
        group.ClipGeometry = new RectangleGeometry(new Rect(definition.X, definition.Y, definition.Width, definition.Height));
        group.Freeze();
        ThemeDrawings[key] = group;
        return group;
    }

    private static void DrawOutline(DrawingContext dc, Geometry geometry, Brush brush, double x, double y)
    {
        dc.PushTransform(new TranslateTransform(x, y));
        dc.DrawGeometry(null, new Pen(brush, 1.2), geometry);
        dc.Pop();
    }

    private static ImageSource? SystemIcon(string path)
    {
        if (SystemIcons.TryGetValue(path, out var icon))
        {
            return icon;
        }

        if (Pending.Add(path))
        {
            // One icon per idle slot keeps scrolling smooth.
            Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                Pending.Remove(path);
                SystemIcons[path] = LoadSystemIcon(path);
                SystemIconLoaded?.Invoke();
            }, DispatcherPriority.Background);
        }

        return null;
    }

    private static BitmapSource? LoadSystemIcon(string path)
    {
        using var measure = Core.Diagnostics.Perf.Enabled ? Core.Diagnostics.Perf.Measure("icon.system", Reporting.PerfDiagnostics.ExtensionKind(path)) : default;
        var info = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static Geometry Freeze(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
