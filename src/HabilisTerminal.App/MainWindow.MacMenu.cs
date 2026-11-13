using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;

namespace HabilisTerminal.App;

/// <summary>
/// The Mac's menu bar (issue #273): the app menu (About, Settings ⌘,, Check for updates; the system adds Hide, Hide
/// Others and Quit), our Left / Files / Commands / Options / Right, built again each time one opens so titles and checks
/// follow the state, and the usual Edit and Window. The menus stay in the window too for now (the owner's choice).
/// Far's keys are not given to the menu bar: it would take them before the window, so only Cmd keys are shown there.
/// </summary>
public partial class MainWindow
{
    private void InstallMacMenu()
    {
        if (!IsMac || Application.Current is not { } application)
        {
            return;
        }

        // The app menu itself is set up by App before the window (the system takes it only then).
        _ = application;
        App.MacAppMenuCommand += command =>
        {
            switch (command)
            {
                case "about":
                    ShowAbout();
                    break;
                case "updates":
                    Fire(CheckForUpdatesAsync());
                    break;
                case "settings":
                    OpenSettingsPage();
                    break;
            }
        };

        var bar = new NativeMenu();
        foreach (var title in MenuTitles)
        {
            var submenu = new NativeMenu();
            var index = bar.Items.Count;
            submenu.NeedsUpdate += (_, _) => FillMenu(submenu, index);
            FillMenu(submenu, index);
            bar.Add(new NativeMenuItem(title) { Menu = submenu });
        }

        bar.Add(new NativeMenuItem("Edit")
        {
            Menu =
            [
                Item("Undo", () => Edit(Key.Z, shift: false), new KeyGesture(Key.Z, KeyModifiers.Meta)),
                Item("Redo", () => Edit(Key.Z, shift: true), new KeyGesture(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift)),
                new NativeMenuItemSeparator(),
                Item("Cut", () => Edit(Key.X, shift: false), new KeyGesture(Key.X, KeyModifiers.Meta)),
                Item("Copy", () => Edit(Key.C, shift: false), new KeyGesture(Key.C, KeyModifiers.Meta)),
                Item("Paste", () => Edit(Key.V, shift: false), new KeyGesture(Key.V, KeyModifiers.Meta)),
                Item("Select All", () => Edit(Key.A, shift: false), new KeyGesture(Key.A, KeyModifiers.Meta)),
            ],
        });
        bar.Add(new NativeMenuItem("Window")
        {
            Menu =
            [
                Item("Minimize", () => WindowState = WindowState.Minimized, new KeyGesture(Key.M, KeyModifiers.Meta)),
                Item("Zoom", _titleBar.ToggleMaximize),
                Item("Enter Full Screen", () => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen,
                    new KeyGesture(Key.F, KeyModifiers.Meta | KeyModifiers.Control)),
                new NativeMenuItemSeparator(),
                Item("New Tab", NewTab, new KeyGesture(Key.T, KeyModifiers.Meta)),
            ],
        });
        NativeMenu.SetMenu(this, bar);
    }

    private void FillMenu(NativeMenu menu, int index)
    {
        menu.Items.Clear();
        foreach (var command in MenuGroups()[index].Commands)
        {
            if (command.IsSeparator)
            {
                menu.Add(new NativeMenuItemSeparator());
                continue;
            }

            var item = Item(command.Label, command.Run!);
            if (command.IsChecked)
            {
                item.ToggleType = MenuItemToggleType.CheckBox;
                item.IsChecked = true;
            }

            menu.Add(item);
        }
    }

    private static NativeMenuItem Item(string header, Action run, KeyGesture? gesture = null)
    {
        var item = new NativeMenuItem(header) { Gesture = gesture };
        item.Click += (_, _) => run();
        return item;
    }

    /// <summary>
    /// Edit's commands: the focused text box or editor does them itself; anywhere else they are the Cmd keys of the
    /// window (the console copies its selection, a panel its paths).
    /// </summary>
    private void Edit(Key key, bool shift)
    {
        var focused = FocusManager?.GetFocusedElement();
        if (focused is TextBox box)
        {
            Action? action = key switch
            {
                Key.Z when shift => box.Redo,
                Key.Z => box.Undo,
                Key.X => box.Cut,
                Key.C => box.Copy,
                Key.V => box.Paste,
                Key.A => box.SelectAll,
                _ => null,
            };
            action?.Invoke();
            return;
        }

        if ((focused as Visual)?.FindAncestorOfType<TextEditor>(includeSelf: true) is { } editor)
        {
            Action? action = key switch
            {
                Key.Z when shift => () => editor.Redo(),
                Key.Z => () => editor.Undo(),
                Key.X => editor.Cut,
                Key.C => editor.Copy,
                Key.V => editor.Paste,
                Key.A => editor.SelectAll,
                _ => null,
            };
            action?.Invoke();
            return;
        }

        HandleMacCommandKey(key, shift ? KeyModifiers.Shift : KeyModifiers.None);
    }
}
