using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HabilisTerminal.App.Theming;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.App.Office;

/// <summary>
/// A full-window page with a Word, Excel or PowerPoint document inside (issue #101): read-only for F3, editable for F4
/// and Shift+F4. Office runs in another process, so its keys never pass through the window; a low-level hook sees the
/// page's own keys while the window is in the foreground (Office's dialogs are windows of their own and keep theirs).
/// </summary>
public sealed class OfficePage : DockPanel, IPage, ILeavePage, IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_F4 = 0x73;
    private const int VK_CONTROL = 0x11;

    private readonly Window _owner;
    private readonly string _path;
    private readonly OfficeApp _kind;
    private readonly bool _readOnly;
    private readonly bool _create;
    private readonly OfficeWindowHost _host = new();
    private readonly TextBlock _status = new() { FontFamily = new FontFamily("Segoe UI"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly LowLevelKeyboardProc _hookProc;
    private OfficeSession? _session;
    private IntPtr _hook;
    private bool _closed;

    public OfficePage(Window owner, string path, OfficeApp kind, bool readOnly, bool create)
    {
        _owner = owner;
        _path = path;
        _kind = kind;
        _readOnly = readOnly;
        _create = create;
        _hookProc = OnKeyboard;
        Focusable = false;
        LastChildFill = true;

        var title = new TextBlock { Text = path, FontFamily = new FontFamily("Segoe UI"), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), Foreground = PanelPalette.DescriptionForeground };
        var hint = new TextBlock { Text = readOnly ? "Read-only · F4 edit · Esc close" : "Ctrl+F4 close", FontFamily = new FontFamily("Segoe UI"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = PanelPalette.DescriptionForeground };
        _status.Foreground = PanelPalette.DescriptionForeground;
        _status.Text = $"Opening in {OfficeFiles.Name(kind)}…";
        var header = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
        DockPanel.SetDock(hint, Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(title);
        header.Children.Add(_status);
        var bar = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1), Background = PanelPalette.HeaderBackground, BorderBrush = PanelPalette.Border };
        DockPanel.SetDock(bar, Dock.Top);
        Children.Add(bar);
        Children.Add(_host);

        _watch.Tick += async (_, _) => await WatchAsync().ConfigureAwait(true);
        Loaded += OnLoaded;
    }

    /// <summary>The page is done; the window closes it.</summary>
    public event Action? CloseRequested;

    /// <summary>F4 in the read-only view.</summary>
    public event Action<string>? EditRequested;

    /// <summary>Office could not open the file; the page is unusable and the window falls back.</summary>
    public event Action<Exception>? Failed;

    public void FocusContent() => _host.FocusDocked();

    /// <summary>Unsaved changes: Save / Don't save / Cancel. False means stay. Office in a dialog of its own keeps the page.</summary>
    public bool ConfirmLeave()
    {
        if (_readOnly || _session is not { } session || _closed)
        {
            return true;
        }

        try
        {
            if (!Pump(session.IsModifiedAsync()))
            {
                return true;
            }

            var body = new TextBlock { Text = $"Save changes to '{Path.GetFileName(_path)}'?" };
            body.SetResourceReference(StyleProperty, "Habi.LabelStyle");
            var answer = ThemedDialog.Show(_owner, "Unsaved changes", body,
                [new DialogButton<int>("_Save", 1, IsDefault: true), new DialogButton<int>("Do_n't save", 2), new DialogButton<int>("Cancel", 0, IsCancel: true)], 0);
            if (answer == 1)
            {
                Pump(WithResult(session.SaveAsync()));
            }

            return answer != 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            OperationDialogs.Message(_owner, OfficeFiles.Name(_kind), $"{OfficeFiles.Name(_kind)} is busy, perhaps in a dialog of its own: finish that first.\n{ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _closed = true;
        _watch.Stop();
        RemoveHook();
        _owner.Activated -= OnOwnerActivated;
        _host.Undock();

        // Still opening: OnLoaded closes the session when it arrives.
        if (_session is { } session)
        {
            // The window is out of the app by now, so waiting here cannot block Office. Bounded: a stuck Office stays behind.
            Task.Run(session.CloseAsync).Wait(TimeSpan.FromSeconds(5));
        }

        _host.Dispose();
    }

    private static async Task<bool> WithResult(Task task)
    {
        await task.ConfigureAwait(false);
        return true;
    }

    /// <summary>Waits while the window goes on pumping: Office talks to its parent window, which must keep answering.</summary>
    private static T Pump<T>(Task<T> task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // From the start: an Office that hangs while opening must not trap the user on this page.
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        try
        {
            var session = await OfficeSession.OpenAsync(_kind, _path, _readOnly, _create).ConfigureAwait(true);
            _session = session;
            if (_closed)
            {
                _ = session.CloseAsync();
                return;
            }

            _host.Dock(session.Window);
            _status.Text = OfficeFiles.Name(_kind);
            _owner.Activated += OnOwnerActivated;
            _watch.Start();
            _host.FocusDocked();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever Office does, it must not take the app down.
            MainWindow.Trace($"office: {ex}");
            if (!_closed)
            {
                Failed?.Invoke(ex);
            }
        }
    }

    private void OnOwnerActivated(object? sender, EventArgs e) => Dispatcher.BeginInvoke(_host.FocusDocked, DispatcherPriority.Input);

    private async Task WatchAsync()
    {
        if (_session is { } session && !_closed && !await session.IsOpenAsync().ConfigureAwait(true) && !_closed)
        {
            // Closed from inside Office: it has already asked about saving.
            _watch.Stop();
            CloseRequested?.Invoke();
        }
    }

    private void RequestClose()
    {
        if (!_closed && ConfirmLeave())
        {
            CloseRequested?.Invoke();
        }
    }

    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message == WM_KEYDOWN && GetForegroundWindow() == new WindowInteropHelper(_owner).Handle)
        {
            var key = Marshal.ReadInt32(data);
            var control = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            Action? action = key switch
            {
                VK_ESCAPE when (_readOnly || _session is null) && !control => RequestClose,
                VK_F4 when _readOnly && _session is not null && !control => () => EditRequested?.Invoke(_path),
                VK_F4 when !_readOnly && control => RequestClose,
                _ => null,
            };
            if (action is not null)
            {
                Dispatcher.BeginInvoke(action);
                return new IntPtr(1);
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private void RemoveHook()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc callback, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? module);
}
