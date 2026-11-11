using System.Windows;
using System.Windows.Interop;
using HabilisTerminal.App.Input;
using HabilisTerminal.App.Widgets;

namespace HabilisTerminal.App;

/// <summary>
/// Terminal clipboard and zoom (SPEC §12). Mouse selection, double / triple click and Shift with mouse-mode programs are
/// handled by the terminal control itself. Copying and pasting go through here: copying because reading the selection
/// clears it, so the control could not see it afterwards (issue #137); pasting so that the multi-line warning and
/// bracketed paste apply to every route (right click, Ctrl+Shift+V).
/// </summary>
public partial class MainWindow
{
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int MK_CONTROL = 0x0008;
    private const int MaxPreviewLines = 6;

    /// <summary>Zero until the first tab exists: restoring the layout activates a panel before that.</summary>
    private IntPtr TerminalHandle => _tab is { } tab ? FindHwndHost(tab.Terminal)?.Handle ?? IntPtr.Zero : IntPtr.Zero;

    private bool TerminalHasFocus => !IsPageOpen && TerminalHandle != IntPtr.Zero && IsChildOrSelf(TerminalHandle, GetFocus());

    private bool OnTerminalMouse(MSG msg, KeyModifiers modifiers)
    {
        if (IsPageOpen || TerminalHandle == IntPtr.Zero || !IsChildOrSelf(TerminalHandle, msg.hwnd))
        {
            return false;
        }

        switch (msg.message)
        {
            // A click in the console works inside it (issue #90); the click itself goes on to the control.
            case WM_LBUTTONDOWN:
                Trace($"console clicked, console mode was {_consoleMode}");
                _consoleMode = true;
                UpdatePanelKeys();

                // The click gives the terminal host the WPF focus as well; only the Win32 focus may stay.
                Dispatcher.BeginInvoke(PutFocusInTerminal, System.Windows.Threading.DispatcherPriority.Input);

                return false;

            // The whole interface zooms (issue #237); the console's font follows UiZoom.Changed.
            case WM_MOUSEWHEEL when ((long)msg.wParam & MK_CONTROL) != 0:
                var delta = (short)(((long)msg.wParam >> 16) & 0xFFFF);
                Widgets.UiZoom.Step(Math.Sign(delta));
                return true;

            case WM_RBUTTONDOWN:
                if (!CopyTerminalSelection())
                {
                    PasteIntoTerminal();
                }

                return true;

            default:
                return false;
        }
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
            text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
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
            var body = new System.Windows.Controls.StackPanel();
            var question = new System.Windows.Controls.TextBlock { Text = $"The text contains {lines.Length} lines. Each line may run as a command." };
            question.SetResourceReference(StyleProperty, "Habi.LabelStyle");
            var sample = new System.Windows.Controls.TextBlock
            {
                Text = preview,
                FontFamily = new System.Windows.Media.FontFamily(TerminalFontFamily + ", Consolas"),
                Margin = new Thickness(0, 8, 0, 0),
                MaxWidth = 520,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            sample.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Habi.Description");
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
        _shell.WriteInput(_shell.Host.PrepareForPaste(text));
    }

    private static void TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Busy clipboard: nothing to do but leave it.
        }
    }
}
