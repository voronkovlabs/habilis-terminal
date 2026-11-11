using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using HabilisTerminal.App.Hosting;
using HabilisTerminal.App.Input;
using HabilisTerminal.App.Layout;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Theming;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Drives;
using HabilisTerminal.Core.FileSystem;
using HabilisTerminal.Core.Panels;
using HabilisTerminal.Shell.ConPty;

namespace HabilisTerminal.App;

/// <summary>Panel commands of the window (SPEC §5).</summary>
public partial class MainWindow
{
    /// <summary>Keys intercepted before they reach either panels or terminal (SPEC §4, §5).</summary>
    private bool OnGlobalKey(Key key, KeyModifiers modifiers)
    {
        using var measure = Core.Diagnostics.Perf.Measure("key");
        Trace($"key {modifiers}+{key} focusedPanel={FocusedPanel()?.Side ?? "-"} wpfLeft={_left.IsKeyboardFocused} wpfRight={_right.IsKeyboardFocused} win32={GetFocus():X}");
        if (HandleZoomKey(key, modifiers))
        {
            return true;
        }

        switch (key)
        {
            // The console grown over the panels (issue #82): Esc at the prompt only collapses it.
            case Key.Escape when modifiers == KeyModifiers.None && CollapseConsoleOnEscape():
                return true;

            // Console tabs work while a program runs, as in Windows Terminal (issue #119).
            case Key.T when modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && !IsPageOpen:
                NewTab();
                return true;

            case Key.Tab when modifiers == KeyModifiers.Control && !IsPageOpen:
                return NextTab(1);

            case Key.Tab when modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && !IsPageOpen:
                return NextTab(-1);

            case Key.W when modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && !IsPageOpen && _tabs.Count > 1 && _tab is not null:
                CloseTab(_tab);
                return true;

            case Key.O when modifiers == KeyModifiers.Control && !IsPageOpen:
                TogglePanels();
                return true;

            case Key.F4 when modifiers == KeyModifiers.Alt:
                Close();
                return true;

            // The command line, from any focus (issue #155).
            case Key.P when modifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                Fire(ShowCommandLineAsync());
                return true;

            // Window-level, from any focus, as in Far.
            case Key.F9 when modifiers == KeyModifiers.Alt:
                _titleBar.ToggleMaximize();
                return true;

            // Terminal-level clipboard; Ctrl+V goes to the shell (SPEC §12). Ctrl+C and Ctrl+Insert copy a selection
            // in the console, as in Windows Terminal; with none they go on to the shell, an interrupt (issue #137).
            // With a panel holding the keys, Ctrl+Shift+C copies its full paths instead (issue #203).
            case Key.C when modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && TerminalHasFocus && FocusedPanel() is null:
                CopyTerminalSelection();
                return true;

            case Key.C or Key.Insert when modifiers == KeyModifiers.Control && TerminalHasFocus && _consoleMode && CopyTerminalSelection():
                return true;

            case Key.V when modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && TerminalHasFocus:
                PasteIntoTerminal();
                return true;
        }

        // A full-window page (viewer, editor, settings) handles its own keys.
        if (IsPageOpen)
        {
            return false;
        }

        if (FocusedPanel() is not { } panel)
        {
            return HandleLayoutKeyInTerminal(key, modifiers);
        }

        // Quick search sees its keys first (Backspace, Esc, Ctrl+Enter...); no key binding may take a typed character
        // meanwhile (issue #39). It arrives as a character (CaptureQuickSearchText), or as text input when a panel
        // holds the WPF focus for a moment (OnPreviewTextInput).
        if (panel.IsQuickSearchActive)
        {
            if (panel.IsQuickSearchTyping(key, modifiers))
            {
                return false;
            }

            if (panel.HandleKey(key, modifiers))
            {
                return true;
            }
        }

        // Enter, arrows, Home / End, Del, Backspace, Esc and Tab act on a command line with text, and an empty line hands
        // them back (OnPanelKeyReturned); Shift selects in the line (issues #85, #88, #89).
        if (panel.HasKeys && ShellIntegrated)
        {
            if (GrayKeyName(key, modifiers) is { } gray)
            {
                _shell?.Host.SendGrayKey(gray);
                return true;
            }

            if (IsCommandLineKey(key, modifiers))
            {
                return false;
            }
        }

        if (OverflowExpanded && IsPanelOnlyKey(key))
        {
            CollapseConsole(stopGrowing: false);
        }

        if (HandleKeyOverHiddenList(panel, key, modifiers))
        {
            return true;
        }

        // Plugin panels and F11 first: a plugin's command key takes precedence over the host's key (issue #49).
        if ((!panel.IsQuickSearchActive && !(panel.ShowsOtherContent && panel.IsPlugin && key != Key.F11) && HandlePluginKey(panel, key, modifiers)) || HandleWindowKey(panel, key, modifiers) || panel.HandleKey(key, modifiers))
        {
            if (OverflowExpanded)
            {
                CollapseConsole(stopGrowing: false); // a Ctrl or Alt command of the panel
            }

            return true;
        }

        // Without the integration script an Enter can only belong to one of them: typing goes into the console, as
        // a click does. Anything else the panel does not take goes on to the console as well.
        if (panel.HasKeys && !ShellIntegrated && modifiers is KeyModifiers.None or KeyModifiers.Shift && (key is Key.None or Key.ImeProcessed || (KeyText.CharOf(key, modifiers == KeyModifiers.Shift) is { } typedChar && !char.IsControl(typedChar))))
        {
            FocusTerminal();
        }

        return false;
    }

    /// <summary>
    /// In a panel showing a quick view or info (issue #128) the list is hidden: the arrows scroll what is shown, as in Far,
    /// and keys that would act on the hidden list's files do nothing. Window keys such as Tab and Ctrl+Q go on.
    /// </summary>
    private static bool HandleKeyOverHiddenList(FilePanel panel, Key key, KeyModifiers modifiers)
    {
        if (!panel.ShowsOtherContent)
        {
            return false;
        }

        switch (modifiers)
        {
            case KeyModifiers.None when key is Key.Up or Key.Down or Key.Left or Key.Right or Key.PageUp or Key.PageDown or Key.Home or Key.End:
                panel.ScrollQuickView(key);
                return true;
            case KeyModifiers.None:
                return key is (>= Key.F3 and <= Key.F8) or Key.Enter or Key.Insert or Key.Delete or Key.Back or Key.Add or Key.Subtract or Key.Multiply;
            case KeyModifiers.Shift:
                return key is (>= Key.F1 and <= Key.F12) or Key.Up or Key.Down or Key.Left or Key.Right or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Enter;
            case KeyModifiers.Alt:
                return key is (>= Key.A and <= Key.Z) or (>= Key.D0 and <= Key.D9) or Key.F5 or Key.F6 or Key.Enter;
            case KeyModifiers.Control:
                return key is Key.A or Key.Enter or Key.PageUp or Key.PageDown;
            default:
                return false;
        }
    }

    /// <summary>
    /// Far's panel keys that also work from the command line. Only at the prompt: a running program (TUI, ssh)
    /// gets every key.
    /// </summary>
    private bool HandleLayoutKeyInTerminal(Key key, KeyModifiers modifiers)
    {
        if (!TerminalHasFocus || _shell?.Host.State != Shell.Integration.ShellState.Idle)
        {
            return false;
        }

        switch (key)
        {
            case Key.P when modifiers == KeyModifiers.Control:
                ToggleInactivePanel();
                return true;
            case Key.F1 when modifiers == KeyModifiers.Alt:
                Fire(ShowDriveMenuAsync(_left));
                return true;
            case Key.F2 when modifiers == KeyModifiers.Alt:
                Fire(ShowDriveMenuAsync(_right));
                return true;
            case Key.F7 when modifiers == KeyModifiers.Alt:
                FindFiles();
                return true;
        }

        if (!AnyPanelVisible || modifiers != KeyModifiers.Control)
        {
            return false;
        }

        switch (key)
        {
            case Key.U:
                SwapPanels(focusPanel: false);
                return true;
            case Key.F1:
                SetPanelVisibility(!Workspace.LeftVisible, Workspace.RightVisible);
                return true;
            case Key.F2:
                SetPanelVisibility(Workspace.LeftVisible, !Workspace.RightVisible);
                return true;
            case Key.L when IsShown(_active):
                ToggleInfoPanel();
                return true;
            case Key.Q when IsShown(_active):
                ToggleQuickView();
                return true;
            default:
                return false;
        }
    }

    private bool HandleWindowKey(FilePanel panel, Key key, KeyModifiers modifiers)
    {
        switch (key)
        {
            case Key.Tab when modifiers == KeyModifiers.None:
                // With one panel shown (Ctrl+P) there is nothing to switch to; the console is reached by a click.
                if (IsShown(Other(panel)))
                {
                    Activate(Other(panel));
                    FocusActivePanel();
                    SyncShellDirectory();
                }

                return true;

            case Key.F1 when modifiers == KeyModifiers.Control:
                SetPanelVisibility(!Workspace.LeftVisible, Workspace.RightVisible);
                return true;

            case Key.F2 when modifiers == KeyModifiers.Control:
                SetPanelVisibility(Workspace.LeftVisible, !Workspace.RightVisible);
                return true;

            case Key.F1 when modifiers == KeyModifiers.Alt:
                Fire(ShowDriveMenuAsync(_left));
                return true;

            case Key.F2 when modifiers == KeyModifiers.Alt:
                Fire(ShowDriveMenuAsync(_right));
                return true;

            case Key.F7 when modifiers == KeyModifiers.Alt:
                FindFiles();
                return true;

            case Key.F12 when modifiers == KeyModifiers.Alt:
                Fire(ShowFolderHistoryAsync());
                return true;

            case Key.F12 when modifiers == KeyModifiers.Control:
                Fire(ShowSortMenuAsync(panel));
                return true;

            case Key.U when modifiers == KeyModifiers.Control:
                SwapPanels();
                return true;

            case Key.P when modifiers == KeyModifiers.Control:
                ToggleInactivePanel();
                return true;

            case Key.L when modifiers == KeyModifiers.Control:
                ToggleInfoPanel();
                return true;

            case Key.Q when modifiers == KeyModifiers.Control:
                ToggleQuickView();
                return true;

            case Key.F3 when modifiers == KeyModifiers.None:
                View(panel);
                return true;

            case Key.F3 when modifiers == KeyModifiers.Alt:
                ViewAlternative(panel);
                return true;

            case Key.F4 when modifiers == KeyModifiers.None:
                Edit(panel);
                return true;

            case Key.F4 when modifiers == KeyModifiers.Shift:
                NewFile(panel);
                return true;

            case Key.Add when modifiers == KeyModifiers.None:
                Fire(SelectByMaskAsync(panel, select: true));
                return true;

            case Key.Subtract when modifiers == KeyModifiers.None:
                Fire(SelectByMaskAsync(panel, select: false));
                return true;

            // File operations (SPEC §8).
            case Key.F5 when modifiers == KeyModifiers.None:
            case Key.F5 when modifiers == KeyModifiers.Shift:
                Fire(CopyOrMoveAsync(panel, move: false, inPlace: modifiers == KeyModifiers.Shift));
                return true;

            case Key.F6 when modifiers == KeyModifiers.None:
            case Key.F6 when modifiers == KeyModifiers.Shift:
                Fire(CopyOrMoveAsync(panel, move: true, inPlace: modifiers == KeyModifiers.Shift));
                return true;

            // Rename what is under the cursor, whatever is marked (issue #159).
            case Key.F2 when modifiers == KeyModifiers.None:
                if (panel.IsPlugin)
                {
                    panel.Message = $"Not supported by {panel.Plugin!.Name}.";
                }
                else
                {
                    Fire(RenameCurrentAsync(panel));
                }

                return true;

            case Key.F7 when modifiers == KeyModifiers.None:
                Fire(MakeDirectoryAsync(panel));
                return true;

            case Key.F8 when modifiers == KeyModifiers.None:
            case Key.Delete when modifiers == KeyModifiers.None:
                Fire(DeleteAsync(panel, permanent: !DeleteToRecycleBin));
                return true;

            case Key.Delete when modifiers == KeyModifiers.Shift:
                Fire(DeleteAsync(panel, permanent: true));
                return true;

            case Key.A when modifiers == KeyModifiers.Control:
                Fire(EditAttributesAsync(panel));
                return true;

            case Key.F10 when modifiers == KeyModifiers.None:
                ConfirmAndExit();
                return true;

            case Key.F9 when modifiers == KeyModifiers.None:
                Fire(ShowTopMenuAsync());
                return true;

            case Key.F1 when modifiers == KeyModifiers.None:
                ShowHelp();
                return true;

            case Key.F11 when modifiers == KeyModifiers.Alt:
                Fire(ShowFileHistoryAsync());
                return true;

            case Key.Enter when modifiers == KeyModifiers.Shift:
                RunDetached(panel);
                return true;

            // Command line from panel focus, as in Far.
            case Key.Enter when modifiers == KeyModifiers.Control:
                InsertIntoCommandLine(ShellQuote.Quote(panel.State.Current.Name));
                return true;

            case Key.OemOpenBrackets when modifiers == KeyModifiers.Control:
                InsertIntoCommandLine(ShellQuote.Quote(_left.CurrentPath));
                return true;

            case Key.Oem6 when modifiers == KeyModifiers.Control: // Ctrl+]
                InsertIntoCommandLine(ShellQuote.Quote(_right.CurrentPath));
                return true;

            case Key.F when modifiers == KeyModifiers.Control:
                InsertIntoCommandLine(ShellQuote.Quote(FullPath(panel, panel.State.Current.Name)));
                return true;

            case Key.E when modifiers == KeyModifiers.Control:
                _shell?.Host.SendKey(Win32InputSequence.VkUp, controlKeyState: Win32InputSequence.EnhancedKey);
                return true;

            case Key.X when modifiers == KeyModifiers.Control:
                _shell?.Host.SendKey(Win32InputSequence.VkDown, controlKeyState: Win32InputSequence.EnhancedKey);
                return true;

            case Key.Space when modifiers == KeyModifiers.Control:
                _shell?.Host.SendKey(Win32InputSequence.VkSpace, ' ', Win32InputSequence.LeftCtrl);
                return true;

            // Clipboard; Ctrl+C / Ctrl+Shift+C as well (issues #54, #203): with a panel focused they never reach the shell.
            case Key.Insert when modifiers == KeyModifiers.Control:
            case Key.C when modifiers == KeyModifiers.Control:
                CopyNames(panel, fullPaths: false);
                return true;

            // Ctrl+Alt+letter is the quick filter (issue #201), so full paths moved to Ctrl+Shift (issue #203).
            case Key.Insert when modifiers == (KeyModifiers.Control | KeyModifiers.Shift):
            case Key.Insert when modifiers == (KeyModifiers.Alt | KeyModifiers.Shift):
            case Key.C when modifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                CopyNames(panel, fullPaths: true);
                return true;

            // Layout.
            case Key.Up when modifiers == KeyModifiers.Control:
                Workspace.TerminalRows++;
                return true;

            case Key.Down when modifiers == KeyModifiers.Control:
                Workspace.TerminalRows = Math.Max(WorkspaceLayout.MinTerminalRows, Workspace.TerminalRows - 1);
                return true;

            case Key.Left when modifiers == KeyModifiers.Control:
            case Key.Right when modifiers == KeyModifiers.Control:
                MoveSplitter(key == Key.Left ? -1 : 1);
                return true;
        }

        return false;
    }

    /// <summary>Starts a UI command; failures are traced and shown instead of disappearing in a forgotten task.</summary>
    private async void Fire(Task command)
    {
        try
        {
            await command;
        }
        catch (Exception ex)
        {
            Trace($"command failed: {ex}");
            OperationDialogs.Message(this, "Unexpected error", ex.Message);
        }
    }

    private static readonly string? TraceFile = Environment.GetEnvironmentVariable("HABI_TRACE");

    /// <summary>Development trace (HABI_TRACE=path) for focus and key routing problems.</summary>
    internal static void Trace(string message)
    {
        if (TraceFile is not null)
        {
            File.AppendAllText(TraceFile, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
    }

    private static string FullPath(FilePanel panel, string name) =>
        name == ".." ? panel.CurrentPath : Path.Combine(panel.CurrentPath, name);

    private void InsertIntoCommandLine(string text) => _shell?.Host.Insert(text + " ");

    private static void CopyNames(FilePanel panel, bool fullPaths)
    {
        var names = panel.State.TargetEntries().Select(e => fullPaths ? FullPath(panel, e.Name) : e.Name).ToArray();
        if (names.Length == 0)
        {
            names = [fullPaths ? panel.CurrentPath : panel.State.Current.Name];
        }

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, names));
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            panel.Message = $"Clipboard is busy: {ex.Message}";
        }
    }

    private void MoveSplitter(int direction)
    {
        var step = Workspace.CellSize.Width / Math.Max(1, Workspace.ActualWidth);
        if (Workspace.LeftVisible && Workspace.RightVisible)
        {
            Workspace.SplitRatio = Math.Clamp(Workspace.SplitRatio + (direction * step), 0.1, 0.9);
        }
        else
        {
            // One panel: Ctrl+Right widens the left panel, Ctrl+Left widens the right one.
            var sign = Workspace.LeftVisible ? direction : -direction;
            Workspace.SinglePanelRatio = Math.Clamp(Workspace.SinglePanelRatio + (sign * step), 0.1, 0.9);
        }
    }

    private void SwapPanels(bool focusPanel = true)
    {
        // Far: visibility belongs to the panel, so a hidden panel stays hidden; with one panel shown (Ctrl+P),
        // the panel and the terminal trade sides.
        (_left, _right) = (_right, _left);
        WorkspaceLayout.SetRole(_left, WorkspaceRole.LeftPanel);
        WorkspaceLayout.SetRole(_right, WorkspaceRole.RightPanel);
        (Workspace.LeftVisible, Workspace.RightVisible) = (Workspace.RightVisible, Workspace.LeftVisible);
        _restoreVisibility = (_restoreVisibility.Right, _restoreVisibility.Left);
        Workspace.InvalidateMeasure();
        if (!IsShown(_active))
        {
            Activate(Other(_active));
            SyncShellDirectory();
        }

        if (focusPanel)
        {
            FocusActivePanel();
        }
    }

    /// <summary>
    /// Ctrl+P: hides the inactive panel, so the terminal takes its place beside the active one; again shows both.
    /// With no panel shown, brings back only the active one, in the shell's folder.
    /// </summary>
    private void ToggleInactivePanel()
    {
        if (Workspace.LeftVisible && Workspace.RightVisible)
        {
            SetPanelVisibility(_active == _left, _active == _right);
        }
        else if (!AnyPanelVisible)
        {
            if (_shell?.Host.CurrentDirectory is { Length: > 0 } shellPath &&
                !string.Equals(shellPath, _active.CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                _ = _active.NavigateAsync(shellPath);
            }

            SetPanelVisibility(_active == _left, _active == _right);
        }
        else
        {
            SetPanelVisibility(true, true);
        }
    }

    /// <summary>Ctrl+L: the info panel of the active panel in the other panel.</summary>
    private void ToggleInfoPanel()
    {
        if (_active.InfoSource is not null)
        {
            var panel = _active;
            panel.InfoSource = null;
            RevealOtherContent(panel, on: false);
            return;
        }

        var other = Other(_active);
        var on = HiddenPanelReveal.TurnsOn(IsShown(other), other.InfoSource is not null);
        other.QuickViewSource = null;
        other.InfoSource = on ? _active : null;
        RevealOtherContent(other, on);
    }

    /// <summary>
    /// Info or quick view turned on in a hidden panel shows it; turned off, a panel shown only for it hides again and
    /// the layout comes back (issue #38). <see cref="SetPanelVisibility"/> keeps the rule of issue #35.
    /// </summary>
    /// <summary>The panel shows its files again; a panel shown only for that content stays shown now.</summary>
    private void LeaveOtherContent(FilePanel panel)
    {
        if (panel.QuickViewSource is null && panel.InfoSource is null)
        {
            return;
        }

        panel.QuickViewSource = null;
        panel.InfoSource = null;
        if (ReferenceEquals(_reveal.Revealed, panel))
        {
            _reveal.Forget();
        }
    }

    private void RevealOtherContent(FilePanel target, bool on)
    {
        var fromTerminal = ConsoleHasKeys;
        if (!(on ? _reveal.ContentOn(target, IsShown(target)) : _reveal.ContentOff(target)))
        {
            return;
        }

        SetPanelVisibility(
            target == _left ? on : Workspace.LeftVisible,
            target == _right ? on : Workspace.RightVisible,
            keepReveal: true);

        // Shown from the prompt, the active panel takes focus (#35); hidden again, focus goes back to the prompt.
        if (on)
        {
            _revealFromTerminal = fromTerminal;
        }
        else if (_revealFromTerminal && !fromTerminal)
        {
            FocusTerminal();
        }
    }

    private static async Task SelectByMaskAsync(FilePanel panel, bool select)
    {
        var result = await QuickPick.ShowAsync(panel, [], new QuickPickOptions
        {
            Title = select ? "Select files" : "Deselect files",
            Placeholder = "Mask, e.g. *.cs,*.xaml|*.g.cs",
            InitialText = "*.*",
            AllowFreeText = true,
        });

        panel.Focus();
        if (result is null || string.IsNullOrWhiteSpace(result.Text))
        {
            return;
        }

        if (!FileMask.TryParse(result.Text, out var mask) || mask is null)
        {
            panel.Message = $"Invalid mask: {result.Text}";
            return;
        }

        if (select)
        {
            panel.State.Selection.SelectByMask(panel.State.Entries, mask);
        }
        else
        {
            panel.State.Selection.DeselectByMask(panel.State.Entries, mask);
        }

        panel.Refresh();
    }

    private static async Task ShowSortMenuAsync(FilePanel panel)
    {
        var sort = panel.State.Sort;
        (string Label, string Keys, Core.FileSystem.SortMode Mode)[] modes =
        [
            ("Name", "Ctrl+F3", Core.FileSystem.SortMode.Name),
            ("Extension", "Ctrl+F4", Core.FileSystem.SortMode.Extension),
            ("Modified", "Ctrl+F5", Core.FileSystem.SortMode.Modified),
            ("Size", "Ctrl+F6", Core.FileSystem.SortMode.Size),
            ("Unsorted", "Ctrl+F7", Core.FileSystem.SortMode.Unsorted),
            ("Created", "Ctrl+F8", Core.FileSystem.SortMode.Created),
            ("Accessed", "Ctrl+F9", Core.FileSystem.SortMode.Accessed),
        ];

        var items = modes.Select(m => new QuickPickItem
        {
            Label = m.Label,
            Icon = m.Mode == sort.Mode ? (sort.Descending ? Codicon.ArrowDown : Codicon.ArrowUp) : string.Empty,
            Detail = m.Keys,
            Tag = m.Mode,
            IsCurrent = m.Mode == sort.Mode,
        }).ToList();
        items.Add(new QuickPickItem { Label = "Directories first", Group = "Options", Icon = sort.DirectoriesFirst ? Codicon.Check : string.Empty, Tag = "dirs" });
        items.Add(new QuickPickItem { Label = "Selected first", Icon = panel.State.SelectedFirst ? Codicon.Check : string.Empty, Detail = "Shift+F12", Tag = "selected" });

        var result = await QuickPick.ShowAsync(panel, items, new QuickPickOptions { Title = "Sort by", Placeholder = "Sort mode" });
        panel.Focus();
        switch (result?.Item?.Tag)
        {
            case Core.FileSystem.SortMode mode:
                panel.State.ApplySortKey(mode);
                break;
            case "dirs":
                panel.State.SetSort(sort with { DirectoriesFirst = !sort.DirectoriesFirst });
                break;
            case "selected":
                panel.State.SetSelectedFirst(!panel.State.SelectedFirst);
                break;
            default:
                return;
        }

        panel.Refresh();
    }

    private async Task ShowFolderHistoryAsync()
    {
        var panel = _active;
        var items = _history.RecentFirst.Select(path => new QuickPickItem
        {
            Label = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path,
            Description = path,
            Icon = Codicon.History,
            Tag = path,
            IsCurrent = string.Equals(path, panel.CurrentPath, StringComparison.OrdinalIgnoreCase),
        }).ToList();

        var result = await QuickPick.ShowAsync(panel, items, new QuickPickOptions { Title = "Folder history", Placeholder = "Filter folders" });
        panel.Focus();
        if (result?.Item?.Tag is string target)
        {
            await NavigateOrOpenAsync(panel, target);
        }
    }

    /// <summary>Alt+F1 / Alt+F2: drives with free space, then places (SPEC §6).</summary>
    private async Task ShowDriveMenuAsync(FilePanel panel)
    {
        var previouslyActive = _active;
        var fromTerminal = ConsoleHasKeys;
        (string Id, string Title)? select = null;
        QuickPickResult? result;
        while (true)
        {
            result = await PickDriveAsync(panel, fromTerminal, select);
            if (result is not { Command: Key.F4 })
            {
                break;
            }

            // F4 (Far): a hotkey for any item but a drive, then the menu opens again on it.
            select = result.Item?.Tag is DriveMenuEntry entry ? entry.Key : null;
            if (select is { } key)
            {
                AssignMenuHotkey(key.Id, key.Title);
            }
        }

        await OnDriveChosenAsync(panel, result, previouslyActive, fromTerminal);
    }

    /// <summary>A drive menu item that is not a drive: its hotkey identity and what it opens (a plugin item or a path).</summary>
    private sealed record DriveMenuEntry((string Id, string Title) Key, object Target);

    /// <summary>F4 in the drive menu (issues #66, #72): a letter or digit for an item; empty goes back to the automatic key.</summary>
    private void AssignMenuHotkey(string id, string title)
    {
        var current = _pluginHotkeys.Get(id, title);
        while (true)
        {
            var box = new System.Windows.Controls.TextBox { Text = current?.ToString() ?? string.Empty, MaxLength = 1, Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
            var body = new System.Windows.Controls.StackPanel();
            var label = new System.Windows.Controls.TextBlock { Text = $"Hotkey for '{title}' (a letter or digit; empty: the automatic one):", Margin = new Thickness(0, 0, 0, 6) };
            label.SetResourceReference(StyleProperty, "Habi.LabelStyle");
            body.Children.Add(label);
            body.Children.Add(box);
            var accepted = ThemedDialog.Show(this, "Hotkey", body,
                [new DialogButton<bool>("OK", true, IsDefault: true), new DialogButton<bool>("Cancel", false, IsCancel: true)], false, box);
            if (!accepted)
            {
                return;
            }

            var text = box.Text.Trim();
            var error = _pluginHotkeys.Assign(id, title, text.Length == 0 ? null : text[0], DriveCatalog.List().Select(d => d.Root[0]));
            if (error is null)
            {
                return;
            }

            OperationDialogs.Message(this, "Hotkey", error);
        }
    }

    private async Task<QuickPickResult?> PickDriveAsync(FilePanel panel, bool fromTerminal, (string Id, string Title)? select)
    {
        var culture = CultureInfo.CurrentCulture;
        var currentRoot = Path.GetPathRoot(panel.CurrentPath) ?? string.Empty;
        var items = new List<QuickPickItem>();
        var drives = DriveCatalog.List();
        foreach (var drive in drives)
        {
            items.Add(new QuickPickItem
            {
                Label = drive.Root.TrimEnd('\\'),
                Description = drive.Type.ToString(),
                Icon = drive.Type switch
                {
                    DriveType.Network => Codicon.NetworkDrive,
                    DriveType.Removable => Codicon.RemovableDrive,
                    DriveType.CDRom => Codicon.Disc,
                    _ => Codicon.Drive,
                },
                Hotkey = drive.Root[0],
                Tag = drive,
                IsCurrent = select is null && string.Equals(drive.Root, currentRoot, StringComparison.OrdinalIgnoreCase),
                Detail = "…",
            });
        }

        var places = DriveCatalog.Places().ToList();
        places.Add(("Other panel", Other(panel).CurrentPath));

        // Everything but the drives gets a hotkey: 1, 2, 3... unless F4 assigned another (issues #66, #72).
        var extras = new List<(DriveMenuEntry Entry, string Label, string Description, string Icon, string Group, char? DefaultHotkey)>();
        foreach (var (plugin, entry) in _plugins.DriveMenuItems())
        {
            extras.Add((new DriveMenuEntry((plugin.Id, entry.Title), (plugin, entry)), entry.Title, plugin.Name, Codicon.Folder, "Plugins", entry.DefaultHotkey));
        }

        foreach (var (name, path) in places)
        {
            var icon = name switch { "Home" => Codicon.Home, "Downloads" => Codicon.Download, "Documents" => Codicon.Document, "Desktop" => Codicon.Desktop, _ => Codicon.Folder };
            extras.Add((new DriveMenuEntry(("places", name), path), name, path, icon, "Places", null));
        }

        var hotkeys = _pluginHotkeys.Resolve([.. extras.Select(e => (e.Entry.Key.Id, e.Entry.Key.Title, e.DefaultHotkey))], drives.Select(d => d.Root[0]));
        string? lastGroup = null;
        for (var i = 0; i < extras.Count; i++)
        {
            var (entry, label, description, icon, group, _) = extras[i];
            items.Add(new QuickPickItem
            {
                Label = hotkeys[i] is { } key ? $"{key}: {label}" : label,
                Description = description,
                Group = group != lastGroup ? group : null,
                Icon = icon,
                Hotkey = hotkeys[i],
                Tag = entry,
                IsCurrent = select == entry.Key,
            });
            lastGroup = group;
        }

        // Sizes arrive per drive; slow or disconnected drives never block the menu.
        foreach (var item in items.Where(i => i.Tag is DriveSummary))
        {
            _ = FillDriveAsync(item, culture);
        }

        return await QuickPick.ShowAsync(panel, items, new QuickPickOptions
        {
            Title = $"Change drive ({panel.Side.ToLowerInvariant()} panel)",
            Placeholder = "Drive letter, hotkey or name, or paste a path or address · F4 changes an item's hotkey",
            IsFreeText = IsPastedLocation,
            OnDelete = item => DisconnectOrEject(item),
            CommandKeys = [Key.F4],
        });
    }

    /// <summary>A full path, or an address with a prefix some plugin registered (issue #142).</summary>
    private bool IsPastedLocation(string text)
    {
        var clean = Core.Panels.TypedLocation.Clean(text);
        return Core.Panels.TypedLocation.IsPath(clean) || _plugins.ForAddress(clean) is not null;
    }

    /// <summary>A pasted place: a file's path goes to its folder with the cursor on it. The disk may be slow or gone, so it is asked off the UI thread.</summary>
    private async Task OpenPastedLocationAsync(FilePanel panel, string text)
    {
        var clean = Core.Panels.TypedLocation.Clean(text);
        if (!Core.Panels.TypedLocation.IsPath(clean))
        {
            await NavigateOrOpenAsync(panel, clean);
            return;
        }

        var path = Path.GetFullPath(Core.Panels.TypedLocation.ToWindowsPath(clean));
        if (await Task.Run(() => File.Exists(path)) && Path.GetDirectoryName(path) is { } folder)
        {
            await panel.NavigateAsync(folder, Path.GetFileName(path));
            return;
        }

        await NavigateOrOpenAsync(panel, path);
    }

    /// <summary>"goto" in the console (issue #152): the active panel goes there, as a paste into the drive menu does.</summary>
    private void OnGoto(string text, string? shellFolder)
    {
        var target = Core.Panels.TypedLocation.ForGoto(text, shellFolder ?? _active.LocalPath, t => _plugins.ForAddress(t) is not null);
        Fire(OnDriveChosenAsync(_active, new QuickPickResult(null, target), _active, fromTerminal: false));
    }

    private async Task OnDriveChosenAsync(FilePanel panel, QuickPickResult? result, FilePanel previouslyActive, bool fromTerminal)
    {
        if (result is { Item: null, Text.Length: > 0 })
        {
            LeaveOtherContent(panel);
            if (!IsShown(panel))
            {
                SetPanelVisibility(panel == _left || Workspace.LeftVisible, panel == _right || Workspace.RightVisible);
            }

            Activate(panel);
            FocusActivePanel();
            await OpenPastedLocationAsync(panel, result.Text);
            SyncShellDirectory();
            return;
        }

        // A choice turns a quick view or info panel back into a file panel at that place, as in Far (issue #75).
        if (result?.Item is not null && result.Command is null)
        {
            LeaveOtherContent(panel);
        }

        if (result?.Item?.Tag is DriveMenuEntry { Target: ValueTuple<Core.Plugins.LoadedPlugin, HabilisTerminal.Plugins.PluginMenuItem> pluginItem })
        {
            if (!IsShown(panel))
            {
                SetPanelVisibility(panel == _left || Workspace.LeftVisible, panel == _right || Workspace.RightVisible);
            }

            await OpenPluginAsync(panel, pluginItem.Item1, pluginItem.Item2.Open);
            return;
        }

        string? target = result?.Item?.Tag switch
        {
            DriveSummary drive => _lastPathPerDrive.TryGetValue(drive.Root, out var last) && Directory.Exists(last) ? last : drive.Root,
            DriveMenuEntry { Target: string path } => path,
            _ => null,
        };

        if (target is null)
        {
            // Cancelled: the layout stays and focus returns where it was (Far).
            if (fromTerminal || !IsShown(previouslyActive))
            {
                FocusTerminal();
            }
            else
            {
                previouslyActive.Focus();
            }

            return;
        }

        // A hidden panel comes back at the chosen place and, picked explicitly, becomes the active one (Far).
        if (!IsShown(panel))
        {
            SetPanelVisibility(panel == _left || Workspace.LeftVisible, panel == _right || Workspace.RightVisible);
        }

        Activate(panel);

        FocusActivePanel();
        await NavigateOrOpenAsync(panel, target);
        SyncShellDirectory();
    }

    private static async Task FillDriveAsync(QuickPickItem item, CultureInfo culture)
    {
        var drive = await DriveCatalog.QueryAsync((DriveSummary)item.Tag!);
        if (!drive.IsReady)
        {
            item.Detail = "not ready";
            return;
        }

        item.Description = string.IsNullOrEmpty(drive.Label) ? $"{drive.Type} · {drive.FileSystem}" : $"{drive.Label} · {drive.FileSystem}";
        item.Detail = $"{PanelColumns.FormatCompactSize(drive.FreeBytes ?? 0, culture)} free of {PanelColumns.FormatCompactSize(drive.TotalBytes ?? 0, culture)}";
        item.Usage = drive.UsedFraction;
    }

    private void DisconnectOrEject(QuickPickItem item)
    {
        if (item.Tag is not DriveSummary drive || drive.Type is not (DriveType.Network or DriveType.Removable))
        {
            return;
        }

        var action = drive.Type == DriveType.Network ? "Disconnect network drive" : "Eject";
        if (MessageBox.Show(this, $"{action} {drive.Root.TrimEnd('\\')}?", "Habilis Terminal", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            if (drive.Type == DriveType.Network)
            {
                DriveCatalog.DisconnectNetworkDrive(drive.Root);
            }
            else
            {
                ShellVerbs.Eject(drive.Root);
            }

            item.Detail = drive.Type == DriveType.Network ? "disconnected" : "ejected";
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            item.Detail = ex.Message;
        }
    }
}
