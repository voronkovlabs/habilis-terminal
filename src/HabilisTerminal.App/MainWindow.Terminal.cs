using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using HabilisTerminal.App.Hosting;
using HabilisTerminal.App.Theming;
using HabilisTerminal.App.Widgets;

namespace HabilisTerminal.App;

/// <summary>
/// Console clipboard and mouse (SPEC §12). Selection, double and triple click are the console view's own. Copying and
/// pasting go through here, so that the multi-line warning and bracketed paste apply to every route (right click,
/// Ctrl+Shift+V).
/// </summary>
public partial class MainWindow
{
    private const int MaxPreviewLines = 6;

    private bool TerminalHasFocus => !IsPageOpen && _tab is { } tab && tab.Terminal.IsFocused;

    /// <summary>The console's own mouse handling, wired once per tab.</summary>
    private void WireTerminalMouse(ConsoleTab tab)
    {
        tab.Terminal.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (tab != _tab || IsPageOpen)
            {
                return;
            }

            var point = e.GetCurrentPoint(tab.Terminal);
            if (point.Properties.IsLeftButtonPressed)
            {
                // A click in the console works inside it (issue #90).
                Trace($"console clicked, console mode was {_consoleMode}");
                _consoleMode = true;
                UpdatePanelKeys();
            }
            else if (point.Properties.IsRightButtonPressed)
            {
                if (!CopyTerminalSelection())
                {
                    PasteIntoTerminal();
                }

                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>Copies the console's selection and clears it. False when nothing is selected.</summary>
    private bool CopyTerminalSelection()
    {
        var text = Terminal.GetSelectedText();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        TrySetClipboard(text);
        Terminal.ClearSelection();
        return true;
    }

    /// <summary>Pastes clipboard text into the shell, asking first for several lines (<c>terminal.multilinePasteWarning</c>).</summary>
    private void PasteIntoTerminal()
    {
        if (_shell is null)
        {
            return;
        }

        string text;
        try
        {
            text = Widgets.Clipboard.ContainsText() ? Widgets.Clipboard.GetText() : string.Empty;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard is held by another program.
            return;
        }

        if (text.Length == 0)
        {
            return;
        }

        var lines = text.TrimEnd('\r', '\n').Split('\n');
        if (lines.Length > 1 && _settings.TerminalMultilinePasteWarning)
        {
            var preview = string.Join("\n", lines.Take(MaxPreviewLines).Select(l => l.TrimEnd('\r'))) + (lines.Length > MaxPreviewLines ? "\n…" : string.Empty);
            var body = new StackPanel();
            var question = new TextBlock { Text = $"The text contains {lines.Length} lines. Each line may run as a command." };
            question.Classes.Add("label");
            var sample = new TextBlock
            {
                Text = preview,
                FontFamily = new FontFamily(Theming.Fonts.MonoWith(TerminalFontFamily)),
                Margin = new Thickness(0, 8, 0, 0),
                MaxWidth = 520,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            sample.SetResourceReference(TextBlock.ForegroundProperty, "Habi.Description");
            body.Children.Add(question);
            body.Children.Add(sample);
            var paste = ThemedDialog.Show(this, $"Paste {lines.Length} lines?", body,
                [new DialogButton<bool>("_Paste", true, IsDefault: true), new DialogButton<bool>("Cancel", false, IsCancel: true)], false);
            PutFocusInTerminal();
            if (!paste)
            {
                return;
            }
        }

        PutFocusInTerminal();
        Terminal.ScrollToBottom();
        _shell.WriteInput(_shell.Host.PrepareForPaste(text));
    }

    private static void TrySetClipboard(string text)
    {
        try
        {
            Widgets.Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Busy clipboard: nothing to do but leave it.
        }
    }
}
