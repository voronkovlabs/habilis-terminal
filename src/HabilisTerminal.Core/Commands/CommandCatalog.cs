using System.Globalization;

namespace HabilisTerminal.Core.Commands;

/// <summary>Where the keyboard is when the command line opens.</summary>
public enum CommandArea
{
    Panel,
    Console,
    Viewer,
    Editor,
    OtherPage,
}

/// <summary>What is under the active panel's cursor.</summary>
public enum CursorKind
{
    None,
    Parent,
    File,
    Folder,
}

/// <summary>What a command is judged by (issue #155): taken when the command line opens, before the focus moves.</summary>
public sealed record CommandContext
{
    public CommandArea Area { get; init; } = CommandArea.Panel;

    /// <summary>A panel is on the screen.</summary>
    public bool PanelsVisible { get; init; } = true;

    public bool ActiveIsLeft { get; init; } = true;

    public bool ActiveIsPlugin { get; init; }

    public bool OtherIsPlugin { get; init; }

    public CursorKind Cursor { get; init; }

    public string? CursorName { get; init; }

    public int Marked { get; init; }

    /// <summary>A plugin panel can copy out.</summary>
    public bool CanGet { get; init; }

    /// <summary>A plugin panel can move out.</summary>
    public bool CanMove { get; init; }

    /// <summary>The other panel is a plugin that takes files.</summary>
    public bool OtherCanPut { get; init; }

    public bool CanDelete { get; init; }

    public bool CanMakeFolder { get; init; }

    public bool PanelHasCommands { get; init; }

    public int ConsoleTabs { get; init; } = 1;

    public bool DeleteToRecycleBin { get; init; } = true;
}

/// <summary>One command: its place in the F9 menu, F1 help and the command line.</summary>
/// <param name="Id">Stable: recent commands are kept by it.</param>
/// <param name="Section">The group in F1 help, and what the command line shows beside the title.</param>
/// <param name="Keys">What runs it, in the notation of the app's key names; null when only the menu and the command line do.</param>
/// <param name="AppliesWhen">False hides it from the command line.</param>
public sealed record AppCommand(string Id, string Section, string Title, string? Keys, Func<CommandContext, bool> AppliesWhen)
{
    /// <summary>The title with what is selected, such as <c>Copy 3 items</c>.</summary>
    public Func<CommandContext, string>? DynamicTitle { get; init; }

    /// <summary>A longer text for F1 help.</summary>
    public string? Help { get; init; }

    /// <summary>Set for what does not run by its key, such as a plugin's command.</summary>
    public Action? Run { get; init; }

    public string TitleIn(CommandContext context) => DynamicTitle?.Invoke(context) ?? Title;
}

/// <summary>
/// Every command of the app, once (issue #155). The F9 menu, F1 help and the command line take their titles and keys from
/// here, so they cannot drift apart. A command runs as its key does, so it cannot differ from the key either.
/// </summary>
public static class CommandCatalog
{
    public const string Layout = "Focus and layout";
    public const string Navigation = "Navigation";
    public const string Selection = "Selection";
    public const string Files = "Files";
    public const string Panels = "Panels";
    public const string CommandLine = "Command line";
    public const string Viewer = "Viewer (F3)";
    public const string Editor = "Editor (F4)";
    public const string Window = "Window";

    private static readonly string[] SectionOrder = [Layout, Navigation, Selection, Files, Panels, CommandLine, Viewer, Editor, Window];

    private static bool Workspace(CommandContext c) => c.Area is CommandArea.Panel or CommandArea.Console;

    private static bool HasPanel(CommandContext c) => Workspace(c) && c.PanelsVisible;

    private static bool Local(CommandContext c) => HasPanel(c) && !c.ActiveIsPlugin;

    private static bool HasItems(CommandContext c) => HasPanel(c) && (c.Marked > 0 || c.Cursor is CursorKind.File or CursorKind.Folder);

    private static bool OnFile(CommandContext c) => HasPanel(c) && c.Cursor == CursorKind.File;

    private static bool OnLocalItem(CommandContext c) => Local(c) && HasItems(c);

    private static bool CanCopy(CommandContext c) => HasItems(c) && (!c.ActiveIsPlugin || c.CanGet) && (c.ActiveIsPlugin ? !c.OtherIsPlugin : !c.OtherIsPlugin || c.OtherCanPut);

    private static bool CanMoveItems(CommandContext c) => HasItems(c) && (!c.ActiveIsPlugin || c.CanMove) && (c.ActiveIsPlugin ? !c.OtherIsPlugin : !c.OtherIsPlugin || c.OtherCanPut);

    private static bool Anywhere(CommandContext c) => true;

    private static string Items(CommandContext c, string verb) =>
        c.Marked > 1 ? string.Create(CultureInfo.InvariantCulture, $"{verb} {c.Marked} items")
        : c.Marked == 0 && c.CursorName is { } name && c.Cursor is CursorKind.File or CursorKind.Folder ? $"{verb} \"{c.CursorName}\""
        : verb;

    public static IReadOnlyList<AppCommand> All { get; } =
    [
        // Focus and layout
        new("panels.toggle", Layout, "Hide or show panels", "Ctrl+O", Workspace),
        new("panels.left", Layout, "Hide or show the left panel", "Ctrl+F1", Workspace),
        new("panels.right", Layout, "Hide or show the right panel", "Ctrl+F2", Workspace),
        new("panels.inactive", Layout, "Hide or show the inactive panel", "Ctrl+P", HasPanel),
        new("panels.swap", Layout, "Swap panels", "Ctrl+U", HasPanel),
        new("console.taller", Layout, "Make the console taller", "Ctrl+Up", Workspace),
        new("console.shorter", Layout, "Make the console shorter", "Ctrl+Down", Workspace),
        new("panels.narrower", Layout, "Make the single panel narrower", "Ctrl+Left", HasPanel),
        new("panels.wider", Layout, "Make the single panel wider", "Ctrl+Right", HasPanel),
        new("window.maximize", Layout, "Maximize or restore the window", "Alt+F9", Anywhere),
        new("window.zoomIn", Layout, "Zoom in", "Ctrl+Alt+=", Anywhere) { Help = "Everything larger; Ctrl+mouse wheel too" },
        new("window.zoomOut", Layout, "Zoom out", "Ctrl+Alt+−", Anywhere) { Help = "Everything smaller; Ctrl+mouse wheel too" },
        new("window.zoomReset", Layout, "Reset zoom", "Ctrl+Alt+0", Anywhere) { Help = "Back to 100%" },

        // Navigation
        new("go.open", Navigation, "Open", "Enter", c => HasPanel(c) && c.Cursor is CursorKind.File or CursorKind.Folder or CursorKind.Parent)
        {
            DynamicTitle = c => c.Cursor == CursorKind.Folder ? $"Open folder \"{c.CursorName}\"" : c.Cursor == CursorKind.Parent ? "Go to the parent folder" : c.CursorName is { } name ? $"Open \"{name}\"" : "Open",
            Help = "Open a folder or archive; run a program in the shell; open other files in their program",
        },
        new("go.runDetached", Navigation, "Run in a new console of its own", "Shift+Enter", c => Local(c) && c.Cursor == CursorKind.File),
        new("go.parent", Navigation, "Parent folder", "Ctrl+PgUp", HasPanel),
        new("go.enter", Navigation, "Open as archive", "Ctrl+PgDn", c => Local(c) && c.Cursor is CursorKind.File or CursorKind.Folder)
        {
            DynamicTitle = c => c.Cursor == CursorKind.Folder ? "Enter the folder" : "Open as archive",
            Help = "Enter the folder under the cursor; on a file: open it as an archive",
        },
        new("go.root", Navigation, "Root of the drive", "Ctrl+\\", Local),
        new("go.driveLeft", Navigation, "Change drive: left panel", "Alt+F1", Workspace) { Help = "Drive menu for the left panel (items have hotkeys 1, 2, 3...; F4 changes one). Paste a path or address into it to go there" },
        new("go.driveRight", Navigation, "Change drive: right panel", "Alt+F2", Workspace) { Help = "Drive menu for the right panel" },
        new("go.folderHistory", Navigation, "Folder history", "Alt+F12", Workspace),
        new("go.fileHistory", Navigation, "View and edit history", "Alt+F11", Workspace),

        // Selection
        new("select.toggle", Selection, "Select or deselect, and move down", "Ins", HasItems),
        new("select.group", Selection, "Select group", "Gray +", HasPanel) { Help = "Select by mask" },
        new("select.ungroup", Selection, "Deselect group", "Gray −", HasPanel) { Help = "Deselect by mask" },
        new("select.invert", Selection, "Invert selection", "Gray *", HasPanel),

        // Files
        new("files.view", Files, "View", "F3", HasItems)
        {
            DynamicTitle = c => Items(c, "View"),
            Help = "View the content as text or hex, whatever the format (on a folder: compute its size)",
        },
        new("files.open", Files, "Show or open in its program", "Alt+F3", c => HasPanel(c) && c.Cursor == CursorKind.File)
        {
            Help = "Pictures and PDF pages shown in the program (in a PDF Ctrl+F opens it in its program, for finding text); other files, Markdown too, in their associated program",
        },
        new("files.edit", Files, "Edit", "F4", c => Local(c) && c.Cursor == CursorKind.File) { DynamicTitle = c => Items(c with { Marked = 0 }, "Edit") },
        new("files.new", Files, "New file", "Shift+F4", Local) { Help = "New file: asks for a name, created when saved" },
        new("files.find", Files, "Find file", "Alt+F7", Local) { Help = "Find files by masks and text, as in Far; Panel puts the results in a temporary panel" },
        new("files.copy", Files, "Copy", "F5", CanCopy) { DynamicTitle = c => Items(c, "Copy") },
        new("files.copyHere", Files, "Copy the item under the cursor", "Shift+F5", c => Local(c) && c.Cursor is CursorKind.File or CursorKind.Folder),
        new("files.move", Files, "Rename or move", "F6", CanMoveItems) { DynamicTitle = c => c.ActiveIsPlugin ? Items(c, "Move") : Items(c, "Rename or move") },
        new("files.rename", Files, "Rename", "F2", c => Local(c) && c.Cursor is CursorKind.File or CursorKind.Folder)
        {
            DynamicTitle = c => c.CursorName is { } name ? $"Rename \"{name}\"" : "Rename",
            Help = "Rename the file or folder under the cursor: only its name. Shift+F6 also moves it",
        },
        new("files.mkdir", Files, "Make folder", "F7", c => HasPanel(c) && (!c.ActiveIsPlugin || c.CanMakeFolder)),
        new("files.delete", Files, "Delete", "F8", c => HasItems(c) && (!c.ActiveIsPlugin || c.CanDelete))
        {
            DynamicTitle = c => Items(c, c.ActiveIsPlugin || !c.DeleteToRecycleBin ? "Delete" : "Delete to Recycle Bin"),
            Help = "Delete (Recycle Bin by default). Del too",
        },
        new("files.deleteNow", Files, "Delete permanently", "Shift+Del", OnLocalItem) { DynamicTitle = c => Items(c, "Delete permanently") },
        new("files.attributes", Files, "Attributes", "Ctrl+A", OnLocalItem),
        new("files.contextMenu", Files, "Windows context menu", "Shift+F10", c => Local(c) && c.Cursor != CursorKind.None) { Help = "Windows context menu. Right click and the Menu key too" },

        // Panels
        new("view.brief", Panels, "View: brief", "Ctrl+1", HasPanel),
        new("view.medium", Panels, "View: medium", "Ctrl+2", HasPanel),
        new("view.full", Panels, "View: full", "Ctrl+3", HasPanel),
        new("view.wide", Panels, "View: wide", "Ctrl+4", HasPanel),
        new("view.detailed", Panels, "View: detailed", "Ctrl+5", HasPanel),
        new("view.alternative", Panels, "View: alternative full", "Ctrl+0", HasPanel),
        new("sort.name", Panels, "Sort by name", "Ctrl+F3", HasPanel),
        new("sort.extension", Panels, "Sort by extension", "Ctrl+F4", Local),
        new("sort.modified", Panels, "Sort by modified", "Ctrl+F5", HasPanel),
        new("sort.size", Panels, "Sort by size", "Ctrl+F6", HasPanel),
        new("sort.unsorted", Panels, "Unsorted", "Ctrl+F7", Local),
        new("sort.created", Panels, "Sort by created", "Ctrl+F8", Local),
        new("sort.accessed", Panels, "Sort by accessed", "Ctrl+F9", Local),
        new("sort.menu", Panels, "Sort by…", "Ctrl+F12", HasPanel),
        new("sort.selectedFirst", Panels, "Selected first", "Shift+F12", HasPanel),
        new("panel.hidden", Panels, "Show hidden files", "Ctrl+H", Local) { Help = "Hidden and system files" },
        new("panel.reread", Panels, "Re-read", "Ctrl+R", HasPanel),
        new("panel.info", Panels, "Info panel", "Ctrl+L", HasPanel) { Help = "Info panel in the other panel (shows a hidden one; again hides it)" },
        new("panel.quickView", Panels, "Quick view", "Ctrl+Q", HasPanel) { Help = "Quick view in the other panel (shows a hidden one; again hides it); on a PDF the mouse wheel turns pages" },
        new("panel.compare", Panels, "Compare folders", null, c => Local(c) && !c.OtherIsPlugin),
        new("tabs.new", Panels, "New console", "Ctrl+Shift+T", Workspace) { Help = "New console, with the panels of the last one closed; each tab keeps its own panels, a long command goes on in its tab" },
        new("tabs.next", Panels, "Next console", "Ctrl+Tab", c => Workspace(c) && c.ConsoleTabs > 1),
        new("tabs.previous", Panels, "Previous console", "Ctrl+Shift+Tab", c => Workspace(c) && c.ConsoleTabs > 1),
        new("tabs.close", Panels, "Close the console", "Ctrl+Shift+W", c => Workspace(c) && c.ConsoleTabs > 1),

        // Command line
        new("line.insertName", CommandLine, "Insert the file name", "Ctrl+Enter", c => HasPanel(c) && c.Cursor is CursorKind.File or CursorKind.Folder),
        new("line.insertFull", CommandLine, "Insert the full file name", "Ctrl+F", c => HasPanel(c) && c.Cursor is CursorKind.File or CursorKind.Folder),
        new("line.insertLeft", CommandLine, "Insert the left panel path", "Ctrl+[", HasPanel),
        new("line.insertRight", CommandLine, "Insert the right panel path", "Ctrl+]", HasPanel),
        new("line.previous", CommandLine, "Previous command", "Ctrl+E", Workspace),
        new("line.next", CommandLine, "Next command", "Ctrl+X", Workspace),
        new("line.copyNames", CommandLine, "Copy names to the clipboard", "Ctrl+Ins", HasItems) { Help = "Copy names to the clipboard (panel focused). Ctrl+C too" },
        new("line.copyFull", CommandLine, "Copy full names to the clipboard", "Ctrl+Shift+Ins", HasItems) { Help = "Copy full names to the clipboard. Ctrl+Alt+C too" },
        new("console.copy", CommandLine, "Copy the console's selection", "Ctrl+Shift+C", c => c.Area == CommandArea.Console) { Help = "Copy the console's selection. A right click, and Ctrl+C or Ctrl+Ins with a selection, too" },
        new("console.paste", CommandLine, "Paste into the console", "Ctrl+Shift+V", c => c.Area == CommandArea.Console) { Help = "Paste into the console. A right click with no selection too" },

        // Viewer
        new("viewer.wrap", Viewer, "Wrap", "F2", c => c.Area == CommandArea.Viewer),
        new("viewer.hex", Viewer, "Hex", "F4", c => c.Area == CommandArea.Viewer),
        new("viewer.find", Viewer, "Find", "F7", c => c.Area == CommandArea.Viewer),
        new("viewer.findNext", Viewer, "Find next", "Shift+F7", c => c.Area == CommandArea.Viewer),
        new("viewer.oem", Viewer, "ANSI or OEM", "F8", c => c.Area == CommandArea.Viewer),
        new("viewer.encoding", Viewer, "Choose the encoding", "Shift+F8", c => c.Area == CommandArea.Viewer),
        new("viewer.goTo", Viewer, "Go to line, offset or percent", "Alt+F8", c => c.Area == CommandArea.Viewer),
        new("viewer.edit", Viewer, "Edit", "F6", c => c.Area == CommandArea.Viewer),

        // Editor
        new("editor.save", Editor, "Save", "F2", c => c.Area == CommandArea.Editor) { Help = "Save. Ctrl+S too" },
        new("editor.saveAs", Editor, "Save as", "Shift+F2", c => c.Area == CommandArea.Editor),
        new("editor.find", Editor, "Find", "F7", c => c.Area == CommandArea.Editor),
        new("editor.findNext", Editor, "Find next", "Shift+F7", c => c.Area == CommandArea.Editor),
        new("editor.replace", Editor, "Replace", "Ctrl+F7", c => c.Area == CommandArea.Editor),
        new("editor.encoding", Editor, "Encoding", "Shift+F8", c => c.Area == CommandArea.Editor),
        new("editor.backToFile", Editor, "Back to the file in the panel", "Ctrl+F10", c => c.Area == CommandArea.Editor),
        new("editor.view", Editor, "View", "F6", c => c.Area == CommandArea.Editor),

        // Window
        new("window.commands", Window, "Command line", "Ctrl+Shift+P", _ => false) { Help = "This command line, in the title bar: commands for what is selected, with their keys; a pasted path or address opens that place" },
        new("window.help", Window, "Keyboard help", "F1", Workspace) { Help = "This help" },
        new("window.menu", Window, "Menu", "F9", Workspace),
        new("window.exit", Window, "Exit", "F10", Workspace),
        new("window.close", Window, "Close this page", "Esc", c => c.Area is CommandArea.Viewer or CommandArea.Editor or CommandArea.OtherPage) { Help = "Close a viewer, editor, settings or this page" },
        new("plugins.menu", Window, "Plugins", "F11", Workspace),
        new("plugins.configure", Window, "Plugin manager…", "Alt+Shift+F9", Workspace),
        new("plugins.panelCommands", Window, "Plugin panel commands…", null, c => HasPanel(c) && c.PanelHasCommands),
        new("settings.open", Window, "Settings", null, Workspace),
        new("settings.file", Window, "Open settings.json", null, Workspace),
        new("settings.theme", Window, "Color theme…", null, Anywhere),
        new("settings.iconTheme", Window, "File icon theme…", null, Anywhere),
        new("app.feedback", Window, "Send feedback…", null, Workspace),
        new("app.performance", Window, "Send performance report", null, Workspace),
        new("app.updates", Window, "Check for updates", null, Workspace),
        new("app.about", Window, "About Habilis Terminal", null, Workspace),
    ];

    /// <summary>What F1 help says that is not a command.</summary>
    private static readonly (string Section, string Keys, string Text)[] Notes =
    [
        (Layout, "Tab", "Switch panels; completes on a command line with text"),
        (Layout, "Typing", "Goes to the command line; the focus stays on the panel, as in Far"),
        (Layout, "Enter, ←/→, Home/End, Del, Backspace", "Edit or run the command line when it has text, act in the panel when it is empty"),
        (Layout, "Esc", "Clear the command line; on an empty line, collapse the console grown over the panels"),
        (Layout, "Click in the console", "Work inside the console (selection, scrollback); Esc on an empty line comes back"),
        (Navigation, "Alt+letter", "Quick search"),
        (Navigation, "Ctrl+Alt+letter", "Quick filter: only names containing the text; Enter keeps it, Esc or the cross in the header clears it"),
        (Navigation, "goto <path>", "Typed in the console: the panel goes there, as a paste into the drive menu does. A file's path opens its folder with the cursor on it; plugin addresses work too. Also relative to the console's folder, with ~ and %VARIABLES%"),
        (Selection, "Shift+arrows", "Select while moving"),
        (Selection, "Ctrl+click", "Toggle selection"),
        (CommandLine, "Ctrl+P, Ctrl+U, Ctrl+L, Ctrl+Q", "Panel keys that also work at the prompt (not while a program runs)"),
        (Editor, "Alt+mouse, Alt+Shift+arrows", "Block selection"),
    ];

    /// <summary>F1 help: sections in order, each with its notes first, then its commands.</summary>
    public static IReadOnlyList<(string Section, IReadOnlyList<(string Keys, string Text)> Rows)> HelpSections { get; } =
        [.. SectionOrder.Select(section => (section, (IReadOnlyList<(string Keys, string Text)>)
        [
            .. Notes.Where(n => n.Section == section).Select(n => (n.Keys, n.Text)),
            .. All.Where(c => c.Section == section && c.Keys is not null).Select(c => (c.Keys!, c.Help ?? c.Title)),
        ]))];

    /// <summary>The keys the app itself answers to: a plugin's global command cannot take one of them.</summary>
    public static IReadOnlySet<string> HostKeys { get; } = All.Where(c => c.Keys is not null).Select(c => c.Keys!).Append("Shift+F6").ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static AppCommand Get(string id) => ById[id];

    private static readonly Dictionary<string, AppCommand> ById = All.ToDictionary(c => c.Id);

    /// <summary>The sections that matter most where the keyboard is, for a command line with nothing typed yet.</summary>
    public static IReadOnlyList<string> SectionsFirst(CommandContext context) => context.Area switch
    {
        CommandArea.Viewer => [Viewer],
        CommandArea.Editor => [Editor],
        CommandArea.Console => [CommandLine, Layout],
        _ when context.Cursor is CursorKind.File or CursorKind.Folder || context.Marked > 0 => [Files, Selection, Navigation],
        _ => [Navigation, Panels],
    };
}
