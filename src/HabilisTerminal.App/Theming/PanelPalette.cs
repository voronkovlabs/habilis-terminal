using System.Windows.Media;

namespace HabilisTerminal.App.Theming;

/// <summary>
/// Brushes for file panels, named after the VS Code workbench keys they will come from in stage 5
/// (Dark Modern values until then).
/// </summary>
internal static class PanelPalette
{
    public static readonly Brush Background = Freeze(0x181818);           // sideBar.background
    public static readonly Brush Foreground = Freeze(0xCCCCCC);           // sideBar.foreground
    public static readonly Brush DescriptionForeground = Freeze(0x9D9D9D); // descriptionForeground
    public static readonly Brush Border = Freeze(0x2B2B2B);               // sideBar.border
    public static readonly Brush HeaderBackground = Freeze(0x1F1F1F);     // sideBarSectionHeader.background
    public static readonly Brush ActiveSelectionBackground = Freeze(0x04395E); // list.activeSelectionBackground
    public static readonly Brush ActiveSelectionForeground = Freeze(0xFFFFFF); // list.activeSelectionForeground
    public static readonly Brush InactiveSelectionBackground = Freeze(0x37373D); // list.inactiveSelectionBackground
    public static readonly Brush FocusOutline = Freeze(0x0078D4);         // list.focusOutline
    public static readonly Brush HoverBackground = Freeze(0x2A2D2E);      // list.hoverBackground
    public static readonly Brush MarkedForeground = Freeze(0xE2C08D);     // gitDecoration.modifiedResourceForeground
    public static readonly Brush FolderIcon = Freeze(0xC5C5C5);           // icon.foreground
    public static readonly Brush FileIcon = Freeze(0x8B8B8B);
    public static readonly Brush ErrorForeground = Freeze(0xF85149);      // errorForeground
    public static readonly Brush InputBackground = Freeze(0x313131);      // input.background
    public static readonly Brush WidgetBackground = Freeze(0x202020);     // quickInput.background
    public static readonly Brush WidgetBorder = Freeze(0x454545);         // widget.border

    private static SolidColorBrush Freeze(int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
