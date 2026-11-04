using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace HabilisTerminal.App.Office;

/// <summary>
/// Keeps a new Office window from flashing on the desktop before it is docked (issue #108). Office cannot simply start
/// hidden: Word and Excel then build no ribbon, and PowerPoint has no hidden document window. So a new window of the
/// class is moved off the screen and made transparent as soon as it is created. Excel is different: off the screen or
/// transparent it does not draw its ribbon, and docked while it is starting it crashes. So its window stays on the
/// screen, right behind the app's window (<paramref name="behind"/>), which covers it. Any window that turns out not to
/// be ours, a document the user opened at that moment, is restored when the hider stops.
/// </summary>
internal sealed class OfficeWindowHider : IDisposable
{
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OffScreen = -32000;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly string _className;
    private readonly IntPtr _behind;
    private readonly int[] _area;
    private readonly WinEventProc _callback;
    private readonly Dictionary<IntPtr, NativeRect> _hidden = [];
    private readonly Dispatcher _dispatcher;
    private IntPtr _hook;

    /// <summary>
    /// The hook lives on a thread of its own: its events arrive through the message queue, and a window busy with its
    /// layout would leave the new Office window on the desktop for a moment.
    /// </summary>
    /// <param name="behind">The window to stay behind, for Excel; zero: off the screen and transparent.</param>
    /// <param name="area">Left, top, width and height on the screen, inside <paramref name="behind"/>.</param>
    public OfficeWindowHider(string className, IntPtr behind = default, int[]? area = null)
    {
        _className = className;
        _behind = behind;
        _area = area ?? [0, 0, 0, 0];
        _callback = OnEvent;
        var started = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            _hook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            started.SetResult(Dispatcher.CurrentDispatcher);

            // Office may place its window again after creating it: back off the screen, until docked.
            var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                foreach (var window in _hidden.Keys)
                {
                    Hide(window);
                }
            };
            timer.Start();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Office window hider",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        _dispatcher = started.Task.GetAwaiter().GetResult();
    }

    /// <summary>The window is docked by now: the host places it.</summary>
    public void Release(IntPtr window) => _dispatcher.Invoke(() =>
    {
        _hidden.Remove(window);
        MakeOpaque(window);
    });

    private void MakeOpaque(IntPtr window)
    {
        if (_behind != IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
        if ((style & WS_EX_LAYERED) != 0)
        {
            SetLayeredWindowAttributes(window, 0, 255, LWA_ALPHA);
            SetWindowLongPtr(window, GWL_EXSTYLE, new IntPtr(style & ~WS_EX_LAYERED));
        }
    }

    public void Dispose()
    {
        _dispatcher.Invoke(() =>
        {
            UnhookWinEvent(_hook);
            foreach (var (window, place) in _hidden)
            {
                SetWindowPos(window, IntPtr.Zero, place.Left, place.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                MakeOpaque(window);
            }
        });
        _dispatcher.InvokeShutdown();
    }

    private void OnEvent(IntPtr hook, uint @event, IntPtr window, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || window == IntPtr.Zero || _hidden.ContainsKey(window))
        {
            return;
        }

        var name = new char[64];
        if (new string(name, 0, GetClassName(window, name, name.Length)) == _className)
        {
            _ = GetWindowRect(window, out var place);
            _hidden.Add(window, place);
            Hide(window);
        }
    }

    private void Hide(IntPtr window)
    {
        if (_behind != IntPtr.Zero)
        {
            SetWindowPos(window, _behind, _area[0], _area[1], _area[2], _area[3], SWP_NOACTIVATE);
            return;
        }

        var style = GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
        if ((style & WS_EX_LAYERED) == 0)
        {
            SetWindowLongPtr(window, GWL_EXSTYLE, new IntPtr(style | WS_EX_LAYERED));
            SetLayeredWindowAttributes(window, 0, 0, LWA_ALPHA);
        }

        if (GetWindowRect(window, out var place) && place.Left != OffScreen)
        {
            SetWindowPos(window, IntPtr.Zero, OffScreen, OffScreen, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    private delegate void WinEventProc(IntPtr hook, uint @event, IntPtr window, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, [Out] char[] name, int capacity);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
