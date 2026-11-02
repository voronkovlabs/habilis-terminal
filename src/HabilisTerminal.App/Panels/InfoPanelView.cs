using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using HabilisTerminal.App.Theming;

namespace HabilisTerminal.App.Panels;

/// <summary>Ctrl+L: drive and directory information about the other (active) panel (SPEC §6).</summary>
public sealed class InfoPanelView : FrameworkElement
{
    private static readonly Typeface UiTypeface = new("Segoe UI");
    private static readonly Typeface UiTypefaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public FilePanel? Source { get; set; }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        dc.DrawRectangle(PanelPalette.Background, null, new Rect(RenderSize));
        if (Source is not { } source)
        {
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        var state = source.State;
        var lines = new List<(string Label, string Value, bool Heading)>();

        if (source.Provider is { } provider)
        {
            // A plugin panel: the plugin's own summary (issue #49), then the location and counts.
            lines.Add((source.Plugin!.Name, string.Empty, true));
            foreach (var line in Plugins.PluginManager.Guard(source.Plugin!, () => provider.Info, []))
            {
                lines.Add((line.Label, line.Value, false));
            }

            lines.Add((string.Empty, string.Empty, false));
            lines.Add(("Location", state.Path, false));
            lines.Add(("Entries", state.Entries.Count.ToString("N0", culture), false));
            lines.Add(("Selected", state.Selection.Count.ToString("N0", culture), false));
            DrawLines(dc, lines);
            return;
        }

        lines.Add(("Drive", string.Empty, true));
        try
        {
            var root = Path.GetPathRoot(state.Path);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                lines.Add(("Root", root, false));
                lines.Add(("Type", drive.DriveType.ToString(), false));
                if (drive.IsReady)
                {
                    lines.Add(("Label", drive.VolumeLabel, false));
                    lines.Add(("File system", drive.DriveFormat, false));
                    lines.Add(("Total", PanelColumns.FormatCompactSize(drive.TotalSize, culture), false));
                    lines.Add(("Free", $"{PanelColumns.FormatCompactSize(drive.AvailableFreeSpace, culture)} ({drive.AvailableFreeSpace * 100.0 / Math.Max(1, drive.TotalSize):0}%)", false));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            lines.Add(("Error", ex.Message, false));
        }

        var files = state.Entries.Where(e => !e.IsDirectory).ToArray();
        lines.Add((string.Empty, string.Empty, false));
        lines.Add(("Directory", string.Empty, true));
        lines.Add(("Path", state.Path, false));
        lines.Add(("Folders", state.Entries.Count(e => e.IsDirectory).ToString("N0", culture), false));
        lines.Add(("Files", files.Length.ToString("N0", culture), false));
        lines.Add(("Size", PanelColumns.FormatCompactSize(files.Sum(e => e.Length), culture), false));
        if (source.FullListingTime is { } full)
        {
            lines.Add(("Read time", $"first screen {source.FirstScreenTime?.TotalMilliseconds:0} ms, all {full.TotalMilliseconds:0} ms", false));
        }

        var (selectedItems, selectedFiles, selectedBytes) = state.SummarizeSelection();
        if (selectedItems > 0)
        {
            lines.Add((string.Empty, string.Empty, false));
            lines.Add(("Selection", string.Empty, true));
            lines.Add(("Items", selectedItems.ToString("N0", culture), false));
            lines.Add(("Files", selectedFiles.ToString("N0", culture), false));
            lines.Add(("Size", PanelColumns.FormatCompactSize(selectedBytes, culture), false));
        }

        DrawLines(dc, lines);
    }

    private void DrawLines(DrawingContext dc, List<(string Label, string Value, bool Heading)> lines)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var y = 12.0;
        foreach (var (label, value, heading) in lines)
        {
            if (heading)
            {
                Draw(dc, label.ToUpperInvariant(), PanelPalette.Foreground, 14, y, ActualWidth - 28, bold: true, pixelsPerDip);
            }
            else if (label.Length > 0)
            {
                Draw(dc, label, PanelPalette.DescriptionForeground, 14, y, 100, bold: false, pixelsPerDip);
                Draw(dc, value, PanelPalette.Foreground, 120, y, ActualWidth - 134, bold: false, pixelsPerDip);
            }

            y += 22;
        }
    }

    private static void Draw(DrawingContext dc, string text, Brush brush, double x, double y, double width, bool bold, double pixelsPerDip)
    {
        if (width <= 4 || text.Length == 0)
        {
            return;
        }

        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? UiTypefaceBold : UiTypeface, 13, brush, pixelsPerDip)
        {
            MaxTextWidth = width,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(formatted, new Point(x, y));
    }
}
