using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HabilisTerminal.App.Theming;
using HabilisTerminal.Shell;
using HabilisTerminal.Shell.Integration;
using HabilisTerminal.Themes;
using Microsoft.Terminal.Wpf;

namespace HabilisTerminal.App.Hosting;

/// <summary>
/// Prototype (issue #122): a frame process, holding no shell, with consoles of other processes inside it as child
/// windows. It tests drawing, focus and what a hung or crashed process does to the others. Not for release.
/// <c>habi --frame</c> starts a frame with two consoles.
/// Ctrl+Shift+Right: next console. Ctrl+Shift+H: this console's UI hangs for 12 s after 2 s. Ctrl+Shift+K: it crashes.
/// </summary>
internal static class FramePrototype
{
    public const int WmConsoleReady = 0x8001; // WM_APP + 1: wParam slot, lParam the console's window
    public const int WmNextConsole = 0x8002;

    public static bool TryStart(Application app)
    {
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Contains("--frame"))
        {
            var frame = new FrameWindow();
            app.MainWindow = frame;
            frame.Show();
            return true;
        }

        var index = Array.IndexOf(arguments, "--console");
        if (index >= 0 && index + 2 < arguments.Length)
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = new ConsoleChild(new IntPtr(long.Parse(arguments[index + 1], System.Globalization.CultureInfo.InvariantCulture)), int.Parse(arguments[index + 2], System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }

        return false;
    }

    public static void Log(string text)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "frame.log"), $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {text}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a prototype's log.
        }
    }

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}

/// <summary>The frame: only the layout, and no shell of its own.</summary>
internal sealed class FrameWindow : Window
{
    private const int GwlStyle = -16;
    private const long WsClipChildren = 0x02000000;
    private const uint SwpNoZorder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>Without it the frame waits for a hung console's window to answer.</summary>
    private const uint SwpAsync = 0x4000;
    private const int WmSetFocus = 0x0007;
    private const int Gap = 4;

    private readonly IntPtr[] _consoles = new IntPtr[2];
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _active;

    public FrameWindow()
    {
        Title = "Habilis frame prototype";
        Width = 1200;
        Height = 800;
        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18));
        Content = new Grid();
        SizeChanged += (_, _) => Arrange2();
        _watch.Tick += (_, _) => Watch();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        FramePrototype.SetWindowLongPtr(handle, GwlStyle, new IntPtr(FramePrototype.GetWindowLongPtr(handle, GwlStyle).ToInt64() | WsClipChildren));
        HwndSource.FromHwnd(handle)!.AddHook(OnMessage);
        for (var slot = 0; slot < 2; slot++)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("--console");
            start.ArgumentList.Add(handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(slot.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var process = Process.Start(start);
        }

        _watch.Start();
    }

    private IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case FramePrototype.WmConsoleReady:
                _consoles[wParam.ToInt32()] = lParam;
                FramePrototype.Log($"console {wParam} is window {lParam:X}");
                Arrange2();
                if (wParam.ToInt32() == _active)
                {
                    FramePrototype.SetFocus(lParam);
                }

                break;
            case FramePrototype.WmNextConsole:
                _active = 1 - wParam.ToInt32();
                if (_consoles[_active] != IntPtr.Zero)
                {
                    FramePrototype.SetFocus(_consoles[_active]);
                }

                FramePrototype.Log($"focus to console {_active}");
                break;
            case WmSetFocus when _consoles[_active] != IntPtr.Zero:
                FramePrototype.SetFocus(_consoles[_active]);
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>A console that has gone leaves its room to the other.</summary>
    private void Watch()
    {
        for (var slot = 0; slot < 2; slot++)
        {
            if (_consoles[slot] != IntPtr.Zero && !FramePrototype.IsWindow(_consoles[slot]))
            {
                FramePrototype.Log($"console {slot} has gone");
                _consoles[slot] = IntPtr.Zero;
                _active = 1 - slot;
                Arrange2();
                if (_consoles[_active] != IntPtr.Zero)
                {
                    FramePrototype.SetFocus(_consoles[_active]);
                }
            }
        }
    }

    private void Arrange2()
    {
        if (Content is not FrameworkElement content || content.ActualWidth <= 0)
        {
            return;
        }

        var scale = VisualTreeHelper.GetDpi(this);
        var origin = content.TranslatePoint(default, this);
        var (x, y) = ((int)(origin.X * scale.DpiScaleX), (int)(origin.Y * scale.DpiScaleY));
        var (width, height) = ((int)(content.ActualWidth * scale.DpiScaleX), (int)(content.ActualHeight * scale.DpiScaleY));
        var both = _consoles[0] != IntPtr.Zero && _consoles[1] != IntPtr.Zero;
        var half = both ? (width - Gap) / 2 : width;
        var left = x;
        foreach (var console in _consoles)
        {
            if (console != IntPtr.Zero)
            {
                FramePrototype.SetWindowPos(console, IntPtr.Zero, left, y, half, height, SwpNoZorder | SwpNoActivate | SwpAsync);
                left += half + Gap;
            }
        }
    }
}

/// <summary>One console in its own process. If the frame goes, it becomes a window of its own and goes on.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Lives as long as the process.")]
internal sealed class ConsoleChild
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WmSetFocus = 0x0007;
    private const int WmDestroy = 0x0002;
    private const int WmKeyDown = 0x0100;
    private const int VkRight = 0x27;
    private const int VkH = 0x48;
    private const int VkK = 0x4B;

    private readonly IntPtr _frame;
    private readonly int _slot;
    private readonly ShellConnection _connection;
    private readonly ColorTheme _theme = ThemeLoader.LoadBuiltIn(ThemeLoader.DarkModern);
    private readonly DispatcherTimer _watchFrame = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private TerminalControl _terminal;
    private HwndSource? _source;
    private bool _closing;
    private bool _alone;

    public ConsoleChild(IntPtr frame, int slot)
    {
        _frame = frame;
        _slot = slot;
        var pwsh = App.FindPwsh() ?? throw new InvalidOperationException("PowerShell 7 (pwsh.exe) was not found.");
        var host = new ShellHost(new ShellHostOptions(pwsh, ShellScripts.IntegrationScriptPath, Environment.CurrentDirectory));
        host.Exited += _ => Application.Current.Dispatcher.BeginInvoke(Exit);
        _connection = new ShellConnection(host);
        _terminal = NewTerminal();

        var parameters = new HwndSourceParameters("HabiConsole", 400, 300)
        {
            ParentWindow = frame,
            WindowStyle = WsChild | WsVisible | WsClipChildren,
        };
        _source = new HwndSource(parameters) { RootVisual = Root(_terminal) };
        _source.AddHook(OnMessage);
        ComponentDispatcher.ThreadFilterMessage += OnKeyMessage;
        _terminal.Loaded += OnTerminalLoaded;
        FramePrototype.PostMessage(frame, FramePrototype.WmConsoleReady, new IntPtr(slot), _source.Handle);
        FramePrototype.Log($"console {slot} started, window {_source.Handle:X}");

        // A killed frame sends nothing: its window simply stops existing.
        _watchFrame.Tick += (_, _) =>
        {
            if (!FramePrototype.IsWindow(_frame))
            {
                BecomeWindow();
            }
        };
        _watchFrame.Start();
    }

    private void OnTerminalLoaded(object sender, RoutedEventArgs e)
    {
        _terminal.Loaded -= OnTerminalLoaded;
        Paint(_terminal);
        _terminal.Connection = _connection;
    }

    private static TerminalControl NewTerminal() => new() { AutoResize = true };

    private static Grid Root(TerminalControl terminal)
    {
        var grid = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18)) };
        grid.Children.Add(terminal);
        return grid;
    }

    private void Paint(TerminalControl terminal)
    {
        var background = _theme.GetOpaque("terminal.background");
        terminal.SetTheme(ThemeService.ToTerminalTheme(_theme), "Cascadia Mono", 12, Color.FromRgb(background.R, background.G, background.B));
    }

    private IntPtr TerminalWindow()
    {
        return Find(_terminal)?.Handle ?? IntPtr.Zero;

        static HwndHost? Find(DependencyObject root)
        {
            if (root is HwndHost host)
            {
                return host;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                if (Find(VisualTreeHelper.GetChild(root, i)) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }

    private IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmSetFocus when TerminalWindow() is var terminal && terminal != IntPtr.Zero:
                FramePrototype.SetFocus(terminal);
                break;
            case WmDestroy when !_closing:
                FramePrototype.Log($"console {_slot}: its window was destroyed");
                Application.Current.Dispatcher.BeginInvoke(BecomeWindow);
                break;
        }

        return IntPtr.Zero;
    }

    private void OnKeyMessage(ref MSG message, ref bool handled)
    {
        if (message.message != WmKeyDown || (GetKeyState(0x11) & 0x8000) == 0 || (GetKeyState(0x10) & 0x8000) == 0)
        {
            return;
        }

        switch (message.wParam.ToInt32())
        {
            case VkRight:
                FramePrototype.PostMessage(_frame, FramePrototype.WmNextConsole, new IntPtr(_slot), IntPtr.Zero);
                handled = true;
                break;
            case VkH:
                handled = true;
                FramePrototype.Log($"console {_slot}: hanging in 2 s for 12 s");
                _ = Task.Delay(2000).ContinueWith(_ => Application.Current.Dispatcher.Invoke(() =>
                {
                    FramePrototype.Log($"console {_slot}: hang starts");
                    Thread.Sleep(12000);
                    FramePrototype.Log($"console {_slot}: hang ends");
                }), TaskScheduler.Default);
                break;
            case VkK:
                FramePrototype.Log($"console {_slot}: crashing");
                Process.GetCurrentProcess().Kill();
                break;
        }
    }

    /// <summary>The window went with the frame: a new control in a window of its own gets the output so far and reads on.</summary>
    private void BecomeWindow()
    {
        if (_alone)
        {
            return;
        }

        _alone = true;
        _watchFrame.Stop();
        var watch = Stopwatch.StartNew();
        FramePrototype.Log($"console {_slot}: the frame has gone, becoming a window");
        _source = null;
        _terminal = NewTerminal();
        var window = new Window { Title = $"Console {_slot}", Width = 800, Height = 500, Content = Root(_terminal) };
        window.Closed += (_, _) => Exit();
        window.Activated += (_, _) => FramePrototype.SetFocus(TerminalWindow());
        _terminal.Loaded += (_, _) =>
        {
            Paint(_terminal);
            _connection.Reattach();
            _terminal.Connection = _connection;
            _connection.ReplayOutput();
            FramePrototype.SetFocus(TerminalWindow());
            FramePrototype.Log($"console {_slot}: a window of its own after {watch.ElapsedMilliseconds} ms");
        };
        window.Show();
    }

    private void Exit()
    {
        _closing = true;
        _connection.Dispose();
        Application.Current.Shutdown();
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);
}
