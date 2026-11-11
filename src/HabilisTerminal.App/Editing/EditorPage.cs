using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using HabilisTerminal.App.Theming;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Editing;
using HabilisTerminal.Core.Settings;
using HabilisTerminal.Core.Viewing;
using HabilisTerminal.Themes;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;

namespace HabilisTerminal.App.Editing;

/// <summary>
/// F4 for text files (SPEC §9): AvalonEdit with Far editor keys — F2 save, Shift+F2 save as, F7 / Shift+F7 find,
/// Ctrl+F7 replace, Shift+F8 encoding, F6 viewer, Ctrl+F10 back to the file in the panel, Esc / F10 close (asks when
/// modified). Saving keeps encoding, BOM and line endings and writes atomically.
/// </summary>
public sealed class EditorPage : DockPanel, IPage
{
    private readonly TextEditor _editor = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _status = new();
    private readonly Window _owner;
    private TextFile _file;
    private string _lastSearch = string.Empty;

    public EditorPage(Window owner, TextFile file, ColorTheme theme, AppSettings settings)
    {
        _owner = owner;
        _file = file;
        Focusable = false;
        LastChildFill = true;

        foreach (var text in new[] { _title, _status })
        {
            text.FontFamily = new FontFamily("Segoe UI");
            text.FontSize = 12;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(0, 0, 16, 0);
        }

        _title.FontWeight = FontWeight.SemiBold;
        var hint = new TextBlock { Text = "F2 or Ctrl+S save · Shift+F2 save as · F7 find · Ctrl+F7 replace · Shift+F8 encoding · Esc close", FontFamily = new FontFamily("Segoe UI"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var header = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
        DockPanel.SetDock(hint, Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(_title);
        header.Children.Add(_status);
        Header = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        DockPanel.SetDock(Header, Dock.Top);
        Children.Add(Header);

        _editor.FontFamily = new FontFamily(settings.EditorFontFamily + ", Consolas");
        _editor.FontSize = settings.EditorFontSize;
        _editor.Options.IndentationSize = settings.EditorTabSize;
        _editor.ShowLineNumbers = true;
        _editor.Options.HighlightCurrentLine = true;
        _editor.Options.EnableRectangularSelection = true;
        _editor.Options.ConvertTabsToSpaces = settings.EditorInsertSpaces;
        _editor.Text = file.Text;
        _editor.Document.UndoStack.ClearAll();
        _editor.Document.UndoStack.MarkAsOriginalFile();
        SyntaxColors.Set(_editor, file.Path, settings.EditorSyntaxHighlighting && !Core.Editing.LongLines.TooLongToColor(file.Text));
        _editor.Document.Changed += OnDocumentChanged;
        _editor.TextArea.Caret.PositionChanged += (_, _) => UpdateStatus();
        _editor.Document.UndoStack.PropertyChanged += (_, _) => UpdateStatus();
        _editor.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, OnEditorKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Children.Add(_editor);

        ApplyTheme(theme);
        UpdateStatus();
    }

    public event Action? CloseRequested;

    /// <summary>F6: open the same file in the viewer.</summary>
    public event Action<string>? ViewRequested;

    /// <summary>Ctrl+F10: back to the panel with the cursor on this file.</summary>
    public event Action<string>? ShowInPanelRequested;

    public string FilePath => _file.Path;

    public bool IsModified => !_editor.Document.UndoStack.IsOriginalFile;

    /// <summary>A long line pasted or joined here would freeze the window as a long line in the file does (issue #163): colours go.</summary>
    private void OnDocumentChanged(object? sender, AvaloniaEdit.Document.DocumentChangeEventArgs e)
    {
        if (!SyntaxColors.IsOn(_editor))
        {
            return;
        }

        var document = _editor.Document;
        var line = document.GetLineByOffset(Math.Min(e.Offset, document.TextLength));
        var last = document.GetLineByOffset(Math.Min(e.Offset + e.InsertionLength, document.TextLength));
        while (line is not null)
        {
            if (line.Length > Core.Editing.LongLines.ColorLimit)
            {
                SyntaxColors.Off(_editor);
                return;
            }

            line = line == last ? null : line.NextLine;
        }
    }

    private Border Header { get; }

    public void FocusContent() => _editor.Focus();

    /// <summary>Editor colours from the theme (<c>editor.*</c>) and syntax colours from its <c>tokenColors</c>.</summary>
    public void ApplyTheme(ColorTheme theme)
    {
        Background = TextEditorTheme.Brush(theme, "editor.background");
        TextEditorTheme.Apply(_editor, theme);
        Header.Background = PanelPalette.HeaderBackground;
        Header.BorderBrush = PanelPalette.Border;
        _title.Foreground = PanelPalette.Foreground;
        _status.Foreground = PanelPalette.DescriptionForeground;
        ((TextBlock)((DockPanel)Header.Child!).Children[0]).Foreground = PanelPalette.DescriptionForeground;
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        var key = e.Key;
        var modifiers = Keyboard.Modifiers;
        var handled = true;
        switch (key)
        {
            case Key.F2 when modifiers == KeyModifiers.None:
            case Key.S when modifiers == KeyModifiers.Control:
                Save(null);
                break;
            case Key.F2 when modifiers == KeyModifiers.Shift:
                Fire(SaveAsAsync());
                break;
            case Key.Escape or Key.F10 when modifiers == KeyModifiers.None:
                if (ConfirmLeave())
                {
                    CloseRequested?.Invoke();
                }

                break;
            case Key.F6 when modifiers == KeyModifiers.None:
                if (ConfirmLeave())
                {
                    ViewRequested?.Invoke(_file.Path);
                }

                break;
            case Key.F10 when modifiers == KeyModifiers.Control:
                if (ConfirmLeave())
                {
                    ShowInPanelRequested?.Invoke(_file.Path);
                }

                break;
            case Key.F7 when modifiers == KeyModifiers.None:
                Fire(FindAsync(prompt: true));
                break;
            case Key.F7 when modifiers == KeyModifiers.Shift:
                Fire(FindAsync(prompt: false));
                break;
            case Key.F7 when modifiers == KeyModifiers.Control:
                Fire(ReplaceAsync());
                break;
            case Key.F8 when modifiers == KeyModifiers.Shift:
                Fire(ChooseEncodingAsync());
                break;
            default:
                handled = false;
                break;
        }

        e.Handled = handled;
    }

    /// <summary>Esc with changes: Save / Don't save / Cancel. False means stay in the editor.</summary>
    public bool ConfirmLeave()
    {
        if (!IsModified)
        {
            return true;
        }

        var body = new TextBlock { Text = $"Save changes to '{Path.GetFileName(_file.Path)}'?" };
        body.Classes.Add("label");
        var answer = ThemedDialog.Show(_owner, "Unsaved changes", body,
            [new DialogButton<int>("_Save", 1, IsDefault: true), new DialogButton<int>("Do_n't save", 2), new DialogButton<int>("Cancel", 0, IsCancel: true)], 0);
        _editor.Focus();
        return answer switch
        {
            1 => Save(null),
            2 => true,
            _ => false,
        };
    }

    /// <summary>F2: atomic save; asks about read-only files and files changed on disk.</summary>
    private bool Save(string? path)
    {
        var target = path ?? _file.Path;
        try
        {
            if (path is null && _file.ChangedOnDisk() && !Ask("File changed on disk", $"'{Path.GetFileName(target)}' was changed by another program. Overwrite it?"))
            {
                return false;
            }

            if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
            {
                if (!Ask("Read-only file", $"'{Path.GetFileName(target)}' is read-only. Clear the attribute and save?"))
                {
                    return false;
                }

                File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
            }

            _file = _file.Save(_editor.Text, path);
            _editor.Document.UndoStack.MarkAsOriginalFile();
            UpdateStatus();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            OperationDialogs.Message(_owner, "Save", ex.Message);
            _editor.Focus();
            return false;
        }
    }

    private bool Ask(string title, string question)
    {
        var body = new TextBlock { Text = question };
        body.Classes.Add("label");
        var yes = ThemedDialog.Show(_owner, title, body, [new DialogButton<bool>("_Yes", true, IsDefault: true), new DialogButton<bool>("_No", false, IsCancel: true)], false);
        _editor.Focus();
        return yes;
    }

    private async Task SaveAsAsync()
    {
        var result = await QuickPick.ShowAsync(this, [], new QuickPickOptions { Title = "Save as", InitialText = _file.Path, AllowFreeText = true });
        _editor.Focus();
        if (result is { Text.Length: > 0 } chosen)
        {
            Save(Path.GetFullPath(chosen.Text.Trim('"')));
        }
    }

    private async Task FindAsync(bool prompt)
    {
        if (prompt || _lastSearch.Length == 0)
        {
            var result = await QuickPick.ShowAsync(this, [], new QuickPickOptions { Title = "Find", InitialText = _lastSearch, AllowFreeText = true });
            _editor.Focus();
            if (result is not { Text.Length: > 0 } chosen)
            {
                return;
            }

            _lastSearch = chosen.Text;
        }

        var text = _editor.Text;
        var start = _editor.SelectionLength > 0 ? _editor.SelectionStart + 1 : _editor.CaretOffset;
        var index = text.IndexOf(_lastSearch, Math.Min(start, text.Length), StringComparison.CurrentCultureIgnoreCase);
        if (index < 0)
        {
            index = text.IndexOf(_lastSearch, StringComparison.CurrentCultureIgnoreCase);
        }

        if (index < 0)
        {
            _status.Text = $"'{_lastSearch}' not found";
            return;
        }

        _editor.Select(index, _lastSearch.Length);
        _editor.ScrollTo(_editor.Document.GetLineByOffset(index).LineNumber, 0);
    }

    /// <summary>Ctrl+F7: replace all occurrences (one undo step).</summary>
    private async Task ReplaceAsync()
    {
        var find = await QuickPick.ShowAsync(this, [], new QuickPickOptions { Title = "Replace: find", InitialText = _lastSearch, AllowFreeText = true });
        if (find is not { Text.Length: > 0 })
        {
            _editor.Focus();
            return;
        }

        var replace = await QuickPick.ShowAsync(this, [], new QuickPickOptions { Title = $"Replace '{find.Text}' with", AllowFreeText = true });
        _editor.Focus();
        if (replace is null)
        {
            return;
        }

        _lastSearch = find.Text;
        var document = _editor.Document;
        var count = 0;
        document.BeginUpdate();
        try
        {
            var offset = 0;
            while (true)
            {
                var index = document.Text.IndexOf(find.Text, offset, StringComparison.CurrentCultureIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                document.Replace(index, find.Text.Length, replace.Text);
                offset = index + replace.Text.Length;
                count++;
            }
        }
        finally
        {
            document.EndUpdate();
        }

        UpdateStatus();
        _status.Text += $"  ·  {count} replaced";
    }

    private async Task ChooseEncodingAsync()
    {
        var items = TextEncoding.Choices.Select(e => new QuickPickItem { Label = e.Name, Tag = e, IsCurrent = e.Name == _file.Encoding.Name }).ToList();
        var result = await QuickPick.ShowAsync(this, items, new QuickPickOptions { Title = IsModified ? "Encoding for saving" : "Encoding" });
        _editor.Focus();
        if (result?.Item?.Tag is not TextEncoding encoding)
        {
            return;
        }

        if (IsModified)
        {
            // With edits the text stays; the file will be saved in the new encoding.
            _file = _file with { Encoding = encoding, HasBom = _file.HasBom && encoding.BomLength > 0 };
        }
        else
        {
            _file = _file.Reinterpret(encoding);
            _editor.Text = _file.Text;
            _editor.Document.UndoStack.ClearAll();
            _editor.Document.UndoStack.MarkAsOriginalFile();
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var caret = _editor.TextArea.Caret;
        _title.Text = (IsModified ? "● " : string.Empty) + _file.Path;
        _status.Text = $"Ln {caret.Line}, Col {caret.Column}  ·  {_file.Encoding.Name}{(_file.HasBom ? " BOM" : string.Empty)}  ·  {_file.LineEnding switch { LineEnding.Lf => "LF", LineEnding.Cr => "CR", _ => "CRLF" }}";
    }

    private async void Fire(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            OperationDialogs.Message(_owner, "Editor", ex.Message);
        }
    }
}
