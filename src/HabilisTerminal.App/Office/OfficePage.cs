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
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_MENU = 0x12;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_F4 = 0x73;
    private const int VK_CONTROL = 0x11;

    private readonly Window _owner;
    private readonly string _path;
    private readonly OfficeApp _kind;
    private readonly bool _readOnly;
    private readonly bool _create;
    private readonly OfficeWindowHost _host = new((PanelPalette.HeaderBackground as SolidColorBrush)?.Color ?? Colors.Black);
    private readonly TextBlock _status = new() { FontFamily = new FontFamily("Segoe UI"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly LowLevelKeyboardProc _hookProc;
    private OfficeSession? _session;
    private IntPtr _hook;
    private bool _closed;
    private bool _closingApp;

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

    /// <summary>Set before the page is disposed with the app.</summary>
    public bool AppExiting { get; set; }

    /// <summary>The page is done; the window closes it.</summary>
    public event Action? CloseRequested;

    /// <summary>F4 in the read-only view.</summary>
    public event Action<string>? EditRequested;

    /// <summary>Office could not open the file; the page is unusable and the window falls back.</summary>
    public event Action<Exception>? Failed;

    public void FocusContent() => _host.FocusDocked();

    /// <summary>
    /// The window is being closed. Office's own close button asks the top-level window to close, which is the app's now:
    /// a request made with the pointer over the document only closes the page. True: the window must stay open.
    /// </summary>
    public bool InterceptWindowClose()
    {
        if (_closingApp || !GetCursorPos(out var pointer) || !GetWindowRect(_host.Handle, out var area)
            || pointer.X < area.Left || pointer.X >= area.Right || pointer.Y < area.Top || pointer.Y >= area.Bottom)
        {
            return false;
        }

        Dispatcher.BeginInvoke(RequestClose);
        return true;
    }

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
            // Office may take seconds: the window goes on meanwhile, unless the app is ending and would take the
            // automation thread with it. The window is out of the app by now, so waiting cannot block Office; bounded,
            // because a stuck Office stays behind.
            var closing = Task.Run(session.CloseAsync);
            if (AppExiting)
            {
                closing.Wait(TimeSpan.FromSeconds(5));
            }
            else if (_owner.IsActive)
            {
                // Office's window leaves the app before it closes, and Windows may activate another program afterwards.
                var owner = _owner;
                closing.ContinueWith(_ => owner.Dispatcher.BeginInvoke(() => owner.Activate()), TaskScheduler.Default);
            }
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
        using var hider = _kind == OfficeApp.Excel
            ? new OfficeWindowHider(OfficeSession.WindowClass(_kind), new WindowInteropHelper(_owner).Handle, _host.ScreenArea())
            : new OfficeWindowHider(OfficeSession.WindowClass(_kind));
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
            hider.Release(session.Window);
            _host.Settle(session.Window);
            _host.Refresh();

            // Excel settles its layout a moment later.
            await Task.Delay(700).ConfigureAwait(true);
            if (!_closed)
            {
                _host.Activate();
                await session.SettleAsync().ConfigureAwait(true);
                _host.Refresh();
            }
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
        if (code >= 0 && message == WM_SYSKEYDOWN && Marshal.ReadInt32(data) == VK_F4 && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0
            && GetForegroundWindow() == new WindowInteropHelper(_owner).Handle)
        {
            // Alt+F4 would go to the document window, which has no frame to close: it means the app, which asks first.
            _closingApp = true;
            Dispatcher.BeginInvoke(() =>
            {
                _owner.Close();
                _closingApp = false; // the user chose to stay
            });
            return new IntPtr(1);
        }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

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
