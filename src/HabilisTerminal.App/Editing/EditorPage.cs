using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HabilisTerminal.App.Theming;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Editing;
using HabilisTerminal.Core.Viewing;
using HabilisTerminal.Themes;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace HabilisTerminal.App.Editing;

/// <summary>
/// F4 for text files (SPEC §9): AvalonEdit with Far editor keys — F2 save, Shift+F2 save as, F7 / Shift+F7 find,
/// Ctrl+F7 replace, Shift+F8 encoding, F6 viewer, Ctrl+F10 back to the file in the panel, Esc / F10 close (asks when
/// modified). Saving keeps encoding, BOM and line endings and writes atomically.
/// </summary>
public sealed class EditorPage : DockPanel
{
    private readonly TextEditor _editor = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _status = new();
    private readonly Window _owner;
    private TextFile _file;
    private string _lastSearch = string.Empty;

    public EditorPage(Window owner, TextFile file, ColorTheme theme)
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

        _title.FontWeight = FontWeights.SemiBold;
        var hint = new TextBlock { Text = "F2 save · Shift+F2 save as · F7 find · Ctrl+F7 replace · Shift+F8 encoding · Esc close", FontFamily = new FontFamily("Segoe UI"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var header = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
        DockPanel.SetDock(hint, Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(_title);
        header.Children.Add(_status);
        Header = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        DockPanel.SetDock(Header, Dock.Top);
        Children.Add(Header);

        _editor.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        _editor.FontSize = 13;
        _editor.ShowLineNumbers = true;
        _editor.Options.HighlightCurrentLine = true;
        _editor.Options.EnableRectangularSelection = true;
        _editor.Options.ConvertTabsToSpaces = false;
        _editor.Text = file.Text;
        _editor.Document.UndoStack.ClearAll();
        _editor.Document.UndoStack.MarkAsOriginalFile();
        _editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(file.Path));
        _editor.TextArea.Caret.PositionChanged += (_, _) => UpdateStatus();
        _editor.Document.UndoStack.PropertyChanged += (_, _) => UpdateStatus();
        _editor.PreviewKeyDown += OnEditorKeyDown;
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

    private Border Header { get; }

    public new bool Focus() => _editor.Focus();

    /// <summary>Editor colours from the theme (<c>editor.*</c>) and syntax colours from its <c>tokenColors</c>.</summary>
    public void ApplyTheme(ColorTheme theme)
    {
        Brush Brush(string key, string fallbackKey = "editor.background")
        {
            var background = theme.GetOpaque("editor.background");
            var color = (theme.Get(key) ?? theme.Get(fallbackKey) ?? background).Over(background);
            var brush = new SolidColorBrush(PanelPalette.ToColor(color));
            brush.Freeze();
            return brush;
        }

        Background = Brush("editor.background");
        _editor.Background = Brush("editor.background");
        _editor.Foreground = Brush("editor.foreground", "foreground");
        _editor.LineNumbersForeground = Brush("editorLineNumber.foreground", "editor.foreground");
        _editor.TextArea.SelectionBrush = Brush("editor.selectionBackground");
        _editor.TextArea.SelectionForeground = null;
        _editor.TextArea.SelectionBorder = null;
        _editor.TextArea.Caret.CaretBrush = Brush("editorCursor.foreground", "editor.foreground");
        _editor.TextArea.TextView.CurrentLineBackground = Brush("editor.lineHighlightBackground");
        _editor.TextArea.TextView.CurrentLineBorder = new Pen(Brush("editor.lineHighlightBorder", "editor.lineHighlightBackground"), 1);
        Header.Background = PanelPalette.HeaderBackground;
        Header.BorderBrush = PanelPalette.Border;
        _title.Foreground = PanelPalette.Foreground;
        _status.Foreground = PanelPalette.DescriptionForeground;
        ((TextBlock)((DockPanel)Header.Child).Children[0]).Foreground = PanelPalette.DescriptionForeground;
        RecolorSyntax(theme);
    }

    private void RecolorSyntax(ColorTheme theme)
    {
        if (_editor.SyntaxHighlighting is not { } definition)
        {
            return;
        }

        var scopes = TokenColors(theme);
        Color? Scope(params string[] candidates)
        {
            // TextMate matching: a rule applies when its scope is a prefix of the token scope; the longest wins.
            foreach (var candidate in candidates)
            {
                Color? best = null;
                var bestLength = -1;
                foreach (var (scope, color) in scopes)
                {
                    if (scope.Length > bestLength && (scope == candidate || candidate.StartsWith(scope + ".", StringComparison.Ordinal)))
                    {
                        best = color;
                        bestLength = scope.Length;
                    }
                }

                if (best is not null)
                {
                    return best;
                }
            }

            return null;
        }

        foreach (var named in definition.NamedHighlightingColors)
        {
            var name = named.Name;
            var color = name.Contains("Punctuation", StringComparison.OrdinalIgnoreCase) ? null
                : name.Contains("Comment", StringComparison.OrdinalIgnoreCase) ? Scope("comment")
                : name.Contains("TrueFalse", StringComparison.OrdinalIgnoreCase) || name.Contains("Null", StringComparison.OrdinalIgnoreCase) ? Scope("constant.language")
                : name.Contains("ThisOrBase", StringComparison.OrdinalIgnoreCase) ? Scope("variable.language", "keyword")
                : name.Contains("String", StringComparison.OrdinalIgnoreCase) || name.Contains("Char", StringComparison.OrdinalIgnoreCase) ? Scope("string")
                : name.Contains("Number", StringComparison.OrdinalIgnoreCase) || name.Contains("Digits", StringComparison.OrdinalIgnoreCase) ? Scope("constant.numeric")
                : name.Contains("Method", StringComparison.OrdinalIgnoreCase) || name.Contains("Function", StringComparison.OrdinalIgnoreCase) ? Scope("entity.name.function")
                : name.Contains("Attribute", StringComparison.OrdinalIgnoreCase) ? Scope("entity.other.attribute-name")
                : name.Contains("Tag", StringComparison.OrdinalIgnoreCase) || name.Contains("Element", StringComparison.OrdinalIgnoreCase) ? Scope("entity.name.tag")
                : name.Contains("Type", StringComparison.OrdinalIgnoreCase) ? Scope("entity.name.type", "storage.type")
                : name.Contains("Preprocessor", StringComparison.OrdinalIgnoreCase) ? Scope("keyword.control.directive", "meta.preprocessor", "keyword")
                : name is "Keywords" || name.Contains("Goto", StringComparison.OrdinalIgnoreCase) || name.Contains("Exception", StringComparison.OrdinalIgnoreCase) ? Scope("keyword.control", "keyword")
                : Scope("storage.modifier", "keyword.other", "keyword");
            named.Foreground = color is { } c ? new SimpleHighlightingBrush(c) : new SimpleHighlightingBrush(((SolidColorBrush)_editor.Foreground).Color);
            named.Background = null;
            named.FontWeight = null;
            named.FontStyle = null;
        }

        // Force AvalonEdit to re-apply the changed colours.
        _editor.SyntaxHighlighting = null;
        _editor.SyntaxHighlighting = definition;
    }

    private static List<(string Scope, Color Color)> TokenColors(ColorTheme theme)
    {
        var result = new List<(string, Color)>();
        foreach (var rule in theme.TokenColors)
        {
            if (!rule.TryGetProperty("settings", out var settings) || !settings.TryGetProperty("foreground", out var foreground) ||
                !ThemeColor.TryParse(foreground.GetString(), out var color))
            {
                continue;
            }

            if (!rule.TryGetProperty("scope", out var scope))
            {
                continue;
            }

            IEnumerable<string> names = scope.ValueKind == JsonValueKind.Array
                ? scope.EnumerateArray().Select(s => s.GetString() ?? string.Empty)
                : (scope.GetString() ?? string.Empty).Split(',');
            foreach (var name in names)
            {
                result.Add((name.Trim(), PanelPalette.ToColor(color)));
            }
        }

        // Later rules win in TextMate themes.
        result.Reverse();
        return result;
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var handled = true;
        switch (key)
        {
            case Key.F2 when modifiers == ModifierKeys.None:
                Save(null);
                break;
            case Key.F2 when modifiers == ModifierKeys.Shift:
                Fire(SaveAsAsync());
                break;
            case Key.Escape or Key.F10 when modifiers == ModifierKeys.None:
                if (ConfirmLeave())
                {
                    CloseRequested?.Invoke();
                }

                break;
            case Key.F6 when modifiers == ModifierKeys.None:
                if (ConfirmLeave())
                {
                    ViewRequested?.Invoke(_file.Path);
                }

                break;
            case Key.F10 when modifiers == ModifierKeys.Control:
                if (ConfirmLeave())
                {
                    ShowInPanelRequested?.Invoke(_file.Path);
                }

                break;
            case Key.F7 when modifiers == ModifierKeys.None:
                Fire(FindAsync(prompt: true));
                break;
            case Key.F7 when modifiers == ModifierKeys.Shift:
                Fire(FindAsync(prompt: false));
                break;
            case Key.F7 when modifiers == ModifierKeys.Control:
                Fire(ReplaceAsync());
                break;
            case Key.F8 when modifiers == ModifierKeys.Shift:
                Fire(ChooseEncodingAsync());
                break;
            default:
                handled = false;
                break;
        }

        e.Handled = handled;
    }

    /// <summary>Esc with changes: Save / Don't save / Cancel.</summary>
    private bool ConfirmLeave()
    {
        if (!IsModified)
        {
            return true;
        }

        var body = new TextBlock { Text = $"Save changes to '{Path.GetFileName(_file.Path)}'?" };
        body.SetResourceReference(StyleProperty, "Habi.LabelStyle");
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
        body.SetResourceReference(StyleProperty, "Habi.LabelStyle");
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
