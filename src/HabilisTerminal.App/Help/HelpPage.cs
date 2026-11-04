using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HabilisTerminal.App.Help;

/// <summary>F1 (SPEC §5): the keyboard map as a full-window page. Esc, F1 or F10 closes it.</summary>
public sealed class HelpPage : DockPanel, IPage
{
    private static readonly (string Section, (string Keys, string Action)[] Rows)[] KeyMap =
    [
        ("Focus and layout",
        [
            ("Tab", "Switch panels; completes on a command line with text"),
            ("Typing", "Goes to the command line; the focus stays on the panel, as in Far"),
            ("Enter, ←/→, Home/End, Del, Backspace", "Edit or run the command line when it has text, act in the panel when it is empty; Shift selects in the line"),
            ("Esc", "Clear the command line; on an empty line, collapse the console grown over the panels"),
            ("Click in the console", "Work inside the console (selection, scrollback); Esc on an empty line comes back"),
            ("Ctrl+O", "Hide / show panels"),
            ("Ctrl+F1 / Ctrl+F2", "Hide / show the left / right panel"),
            ("Ctrl+P", "Hide / show the inactive panel"),
            ("Ctrl+U", "Swap panels"),
            ("Ctrl+Up / Ctrl+Down", "Terminal height"),
            ("Ctrl+Left / Ctrl+Right", "Single-panel width"),
            ("Alt+F9", "Maximize / restore the window"),
        ]),
        ("Navigation",
        [
            ("Enter", "Open a folder or archive; run a program in the shell; open other files in their program"),
            ("Shift+Enter", "Run a program in a new console of its own"),
            ("Ctrl+PgUp", "Parent folder"),
            ("Ctrl+PgDn", "Enter the folder under the cursor; on a file: open it as an archive"),
            ("Ctrl+\\", "Root of the drive"),
            ("Alt+F1 / Alt+F2", "Drive menu for the left / right panel (items have hotkeys 1, 2, 3...; F4 changes one)"),
            ("Alt+F12", "Folder history"),
            ("Alt+F11", "View and edit history"),
            ("Alt+letter", "Quick search"),
        ]),
        ("Selection",
        [
            ("Ins", "Toggle selection and move down"),
            ("Shift+arrows", "Select while moving"),
            ("Ctrl+click", "Toggle selection"),
            ("Gray +  /  Gray −", "Select / deselect by mask"),
            ("Gray *", "Invert selection"),
        ]),
        ("Files",
        [
            ("F3", "View (on a folder: compute its size)"),
            ("Alt+F3", "Markdown rendered; other files in their associated program"),
            ("F4 / Shift+F4", "Edit / new file: asks for a name, created when saved"),
            ("F5 / Shift+F5", "Copy / copy the item under the cursor"),
            ("F6 / Shift+F6", "Move or rename / rename the item under the cursor"),
            ("F7", "Make folder"),
            ("F8 / Del", "Delete (Recycle Bin by default)"),
            ("Shift+Del", "Delete permanently"),
            ("Ctrl+A", "Attributes"),
            ("Right click, Shift+F10, Menu key", "Windows context menu"),
        ]),
        ("Panels",
        [
            ("Ctrl+1 … Ctrl+5, Ctrl+0", "View modes"),
            ("Ctrl+F3 … Ctrl+F9", "Sort by name / extension / modified / size / unsorted / created / accessed"),
            ("Ctrl+F12", "Sort menu"),
            ("Shift+F12", "Selected first"),
            ("Ctrl+H", "Hidden and system files"),
            ("Ctrl+R", "Re-read"),
            ("Ctrl+L", "Info panel in the other panel (shows a hidden one; again hides it)"),
            ("Ctrl+Q", "Quick view in the other panel (shows a hidden one; again hides it)"),
        ]),
        ("Command line",
        [
            ("Ctrl+Enter", "Insert the file name"),
            ("Ctrl+F", "Insert the full file name"),
            ("Ctrl+[ / Ctrl+]", "Insert the left / right panel path"),
            ("Ctrl+E / Ctrl+X", "Previous / next command"),
            ("Ctrl+Ins, Ctrl+C", "Copy names to the clipboard (panel focused)"),
            ("Ctrl+Alt+Ins, Ctrl+Alt+C", "Copy full names to the clipboard"),
            ("Ctrl+P, Ctrl+U, Ctrl+L, Ctrl+Q", "Panel keys that also work at the prompt (not while a program runs)"),
        ]),
        ("Viewer (F3)",
        [
            ("F2", "Wrap"),
            ("F4", "Hex"),
            ("F7 / Shift+F7", "Find / find next"),
            ("F8 / Shift+F8", "ANSI ↔ OEM / choose the encoding"),
            ("Alt+F8", "Go to line, offset or percent"),
            ("F6", "Edit"),
        ]),
        ("Editor (F4)",
        [
            ("F2 / Shift+F2", "Save / save as"),
            ("F7 / Shift+F7", "Find / find next"),
            ("Ctrl+F7", "Replace"),
            ("Shift+F8", "Encoding"),
            ("Alt+mouse, Alt+Shift+arrows", "Block selection"),
            ("Ctrl+F10", "Back to the file in the panel"),
            ("F6", "View"),
        ]),
        ("Window",
        [
            ("F1", "This help"),
            ("F9", "Menu"),
            ("F10", "Exit"),
            ("Esc", "Close a viewer, editor, settings or this page"),
        ]),
    ];

    private readonly ScrollViewer _scroll;

    public HelpPage()
    {
        LastChildFill = true;
        SetResourceReference(BackgroundProperty, "Habi.Background");

        var title = Text("Keyboard", 12, FontWeights.SemiBold, "Habi.Foreground");
        var hint = Text("Esc close", 12, FontWeights.Normal, "Habi.Description");
        var header = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
        DockPanel.SetDock(hint, Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(title);
        var headerBorder = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder.SetResourceReference(Border.BackgroundProperty, "Habi.WidgetBackground");
        headerBorder.SetResourceReference(Border.BorderBrushProperty, "Habi.WidgetBorder");
        SetDock(headerBorder, Dock.Top);
        Children.Add(headerBorder);

        var columns = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 16, 24, 24) };
        foreach (var (section, rows) in KeyMap)
        {
            var grid = new Grid { Width = 460, Margin = new Thickness(0, 0, 32, 24) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = Text(section, 14, FontWeights.SemiBold, "Habi.Foreground");
            heading.Margin = new Thickness(0, 0, 0, 6);
            Grid.SetColumnSpan(heading, 2);
            grid.Children.Add(heading);
            foreach (var (keys, action) in rows)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var keyText = Text(keys, 13, FontWeights.Normal, "Habi.Accent");
                keyText.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                keyText.FontSize = 12;
                var actionText = Text(action, 13, FontWeights.Normal, "Habi.Foreground");
                actionText.TextWrapping = TextWrapping.Wrap;
                foreach (var cell in new[] { keyText, actionText })
                {
                    cell.Margin = new Thickness(0, 2, 8, 2);
                    Grid.SetRow(cell, grid.RowDefinitions.Count - 1);
                    grid.Children.Add(cell);
                }

                Grid.SetColumn(actionText, 1);
            }

            columns.Children.Add(grid);
        }

        _scroll = new ScrollViewer { Content = columns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = true, FocusVisualStyle = null };
        Children.Add(_scroll);
        PreviewKeyDown += OnKeyDown;
    }

    public event Action? CloseRequested;

    public void FocusContent() => _scroll.Focus();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F1 or Key.F10 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            CloseRequested?.Invoke();
        }
    }

    private static TextBlock Text(string text, double size, FontWeight weight, string brushKey)
    {
        var block = new TextBlock { Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = size, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }
}
