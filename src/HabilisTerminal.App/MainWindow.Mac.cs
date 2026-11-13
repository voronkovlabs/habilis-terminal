using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Input;
using HabilisTerminal.App.Panels;
using HabilisTerminal.Core.Panels;

namespace HabilisTerminal.App;

/// <summary>
/// The Cmd keys of a Mac (issue #272): what Mac programs do with them, next to Far's keys, which stay. Only on macOS;
/// Windows has no Cmd key and keeps its keys as they are.
/// </summary>
public partial class MainWindow
{
    private static bool IsMac => OperatingSystem.IsMacOS();

    /// <summary>
    /// A key with Cmd held. True when the window did something with it; false leaves it to the focused control (a text
    /// box or the editor do Cmd+C / V / X / A / Z themselves). A Cmd key never reaches Far's keys as a plain letter.
    /// </summary>
    private bool HandleMacCommandKey(Key key, KeyModifiers modifiers)
    {
        var shift = modifiers == KeyModifiers.Shift;
        var plain = modifiers == KeyModifiers.None;
        var panel = FocusedPanel();
        var inConsole = panel is null && TerminalHasFocus && !IsPageOpen;
        switch (key)
        {
            case Key.Q when plain:
                Close();
                return true;
            case Key.OemComma when plain:
                OpenSettingsPage();
                return true;
            case Key.P when shift:
                Fire(ShowCommandLineAsync());
                return true;

            // As Safari and the Finder zoom: Cmd+= / Cmd+- / Cmd+0 (Ctrl+Alt+= / - / 0 stay).
            case Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract or Key.D0 or Key.NumPad0 when plain:
                return HandleZoomKey(key, KeyModifiers.Control | KeyModifiers.Alt);
        }

        if (IsPageOpen)
        {
            return false; // the viewer and the editor have their own keys
        }

        switch (key)
        {
            case Key.T when plain:
                NewTab();
                return true;
            case Key.W when plain && _tabs.Count > 1 && _tab is { } tab:
                CloseTab(tab);
                return true;
            case Key.F when plain:
                FindFiles();
                return true;
        }

        if (inConsole)
        {
            switch (key)
            {
                case Key.C when plain:
                    CopyTerminalSelection();
                    return true;
                case Key.V when plain:
                    PasteIntoTerminal();
                    return true;
                case Key.K when plain:
                    ClearConsole();
                    return true;
                default:
                    return true; // no Cmd key types into the shell
            }
        }

        if (panel is null)
        {
            return false;
        }

        // The Finder's keys in a panel.
        switch (key)
        {
            case Key.C when plain:
                CopyNames(panel, fullPaths: true);
                return true;
            case Key.V when plain:
                PasteIntoTerminal();
                return true;
            case Key.A when plain:
                PanelKey(panel, Key.Add, KeyModifiers.Shift); // Far's Shift+Gray +
                return true;
            case Key.Back when plain:
                PanelKey(panel, Key.F8, KeyModifiers.None); // to the Trash, as deleting does by default
                return true;
            case Key.Up when plain:
                PanelKey(panel, Key.PageUp, KeyModifiers.Control); // the folder above
                return true;
            case Key.Down or Key.O when plain:
                PanelKey(panel, Key.Enter, KeyModifiers.None);
                return true;
            case Key.OemPeriod when shift:
                PanelKey(panel, Key.H, KeyModifiers.Control); // hidden files, as Cmd+Shift+. in the Finder
                return true;
            case Key.N when shift:
                PanelKey(panel, Key.F7, KeyModifiers.None); // a new folder
                return true;
            case Key.I when plain:
                PanelKey(panel, Key.A, KeyModifiers.Control); // Get Info: the attributes
                return true;
            case Key.R when plain:
                PanelKey(panel, Key.R, KeyModifiers.Control); // read again
                return true;
            case Key.Y when plain:
                QuickLook(panel, Targets(panel)); // as Cmd+Y in the Finder
                return true;
            case Key.R when shift:
                RevealInFinder(panel, Targets(panel));
                return true;
            default:
                return true;
        }
    }

    /// <summary>Cmd+K, as in Terminal: the scrollback goes, and the shell draws its prompt at the top.</summary>
    private void ClearConsole()
    {
        if (_tab is not { } tab)
        {
            return;
        }

        tab.Connection.Emulator.Feed("\u001b[H\u001b[2J\u001b[3J");
        tab.Host.Write("\f");
    }

    private void FillScreenOnce(object? sender, EventArgs e)
    {
        Opened -= FillScreenOnce;
        if (Screens.ScreenFromWindow(this) is not { } screen)
        {
            return;
        }

        var area = screen.WorkingArea;
        Position = area.Position;
        Width = area.Width / screen.Scaling;
        Height = area.Height / screen.Scaling;
    }

    /// <summary>Cmd+click marks a file, as Insert does in Far: a Mac keyboard has no Insert.</summary>
    internal static KeyModifiers MarkModifier => IsMac ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>
    /// Enter runs a program in the console: on Windows by its extension (PATHEXT), elsewhere when the file may be run
    /// (its x bit) and is not a package, which the system opens (#274).
    /// </summary>
    private static bool IsRunnable(FilePanel panel, PanelItem item)
    {
        if (OperatingSystem.IsWindows())
        {
            return Core.FileSystem.RunnableFiles.IsRunnable(item.Name);
        }

        if (item.Entry.IsDirectory)
        {
            return false;
        }

        try
        {
            var mode = File.GetUnixFileMode(Path.Combine(panel.CurrentPath, item.Name));
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The marked items, else the one under the cursor; full paths.</summary>
    private static List<string> Targets(FilePanel panel)
    {
        var names = panel.State.Selection.Count > 0 ? [.. panel.State.Selection.Names] : panel.State.Current.IsParent ? [] : new List<string> { panel.State.Current.Name };
        return [.. names.Select(n => Path.Combine(panel.CurrentPath, n))];
    }

    /// <summary>The Finder's Quick Look for the items (qlmanage, the system's own previewer).</summary>
    private static void QuickLook(FilePanel panel, List<string> paths) => RunTool(panel, "qlmanage", ["-p", .. paths]);

    /// <summary>A Finder window on the folder, the first item selected.</summary>
    private static void RevealInFinder(FilePanel panel, List<string> paths) =>
        RunTool(panel, "open", paths.Count > 0 ? ["-R", paths[0]] : [panel.CurrentPath]);

    private static void RunTool(FilePanel panel, string tool, List<string> arguments)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            panel.Message = $"{tool}: {ex.Message}";
        }
    }

    /// <summary>
    /// Right click on a Mac (#274): the Finder's commands for the items, as the system has no context menu to borrow
    /// from another program the way Explorer lends its own.
    /// </summary>
    private void ShowMacItemMenu(FilePanel panel, IReadOnlyList<string> names)
    {
        var paths = names.Select(n => Path.Combine(panel.CurrentPath, n)).ToList();
        MenuItem Item(string header, string? keys, Action run)
        {
            var item = new MenuItem { Header = header, InputGesture = keys is null ? null : KeyGesture.Parse(keys) };
            item.Click += (_, _) => run();
            return item;
        }

        var items = new List<Control>();
        if (paths.Count > 0)
        {
            items.Add(Item("Open", "Cmd+O", () => PanelKey(panel, Key.Enter, KeyModifiers.None)));
            items.Add(Item("Quick Look", "Cmd+Y", () => QuickLook(panel, paths)));
            items.Add(Item("Show in Finder", "Cmd+Shift+R", () => RevealInFinder(panel, paths)));
            items.Add(new Separator());
            items.Add(Item("Copy Path", null, () => Platform.Desktop.Current.SetClipboardText(string.Join('\n', paths))));
            items.Add(Item("Rename…", "Shift+F6", () => PanelKey(panel, Key.F6, KeyModifiers.Shift)));
            items.Add(Item("Get Info", "Cmd+I", () => PanelKey(panel, Key.A, KeyModifiers.Control)));
            items.Add(new Separator());
            items.Add(Item("Move to Trash", "Cmd+Back", () => PanelKey(panel, Key.F8, KeyModifiers.None)));
        }
        else
        {
            items.Add(Item("Show in Finder", null, () => RevealInFinder(panel, [])));
            items.Add(Item("New Folder", "Cmd+Shift+N", () => PanelKey(panel, Key.F7, KeyModifiers.None)));
        }

        new ContextMenu { ItemsSource = items }.Open(panel);
    }

    /// <summary>
    /// A folder or file dropped on the Dock icon, or opened with the program from the Finder (#275): the active panel
    /// goes there, a file under its cursor.
    /// </summary>
    private void InstallMacActivation()
    {
        if (!IsMac || Avalonia.Application.Current?.TryGetFeature(typeof(Avalonia.Controls.ApplicationLifetimes.IActivatableLifetime)) is not Avalonia.Controls.ApplicationLifetimes.IActivatableLifetime lifetime)
        {
            return;
        }

        lifetime.Activated += (_, e) =>
        {
            if (e is Avalonia.Controls.ApplicationLifetimes.FileActivatedEventArgs files &&
                files.Files.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null) is { } path)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Fire(OpenPastedLocationAsync(_active, path)));
            }
        };
    }
}
