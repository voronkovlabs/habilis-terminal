using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace HabilisTerminal.App.Widgets;

/// <summary>
/// A window shown modally the WPF way (issue #253): <see cref="ShowModal"/> returns when it closes, with the
/// <see cref="DialogResult"/> it was given. Avalonia's own <c>ShowDialog</c> is asynchronous; the callers here ask and act
/// on the answer in one go, so a nested dispatcher frame waits instead.
/// </summary>
public class ModalWindow : Window
{
    private bool? _dialogResult;

    public ModalWindow()
    {
        // Arrows move between buttons and controls, as in Far dialogs (issue #294).
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!e.Handled && e.KeyModifiers == KeyModifiers.None && DialogArrows.Move(this, e.Key))
            {
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>Setting it closes the window, as in WPF.</summary>
    public bool? DialogResult
    {
        get => _dialogResult;
        set
        {
            _dialogResult = value;
            Close();
        }
    }

    /// <summary>The window to centre on and stay above; null before the main window exists.</summary>
    public Window? ModalOwner { get; set; }

    /// <summary>Shows the window modally and waits for it to close.</summary>
    public bool? ShowModal()
    {
        var frame = new DispatcherFrame();
        Closed += (_, _) => frame.Continue = false;
        if (ModalOwner is { IsVisible: true } owner)
        {
            _ = ShowDialog(owner);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Show();
        }

        Avalonia.Threading.Dispatcher.UIThread.PushFrame(frame);
        ModalOwner?.Activate();
        return _dialogResult;
    }
}
