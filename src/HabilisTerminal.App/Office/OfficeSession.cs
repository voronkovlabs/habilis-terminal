using System.Runtime.InteropServices;
using System.Windows.Threading;
using HabilisTerminal.Core.Viewing;

namespace HabilisTerminal.App.Office;

/// <summary>
/// One document in Word, Excel or PowerPoint, driven by COM automation on a thread of its own (issue #101): Office can
/// take seconds or sit in a dialog of its own, and neither may block the window. All calls are late bound, so the app
/// needs no Office assemblies and works with whatever version is installed.
/// </summary>
public sealed class OfficeSession
{
    private const int CallRejected = unchecked((int)0x80010001);
    private const int ServerBusy = unchecked((int)0x8001010A);

    private readonly OfficeApp _kind;
    private readonly Dispatcher _dispatcher;
    private dynamic? _app;
    private dynamic? _document;

    private OfficeSession(OfficeApp kind, Dispatcher dispatcher)
    {
        _kind = kind;
        _dispatcher = dispatcher;
    }

    /// <summary>The document window, which the page docks.</summary>
    public IntPtr Window { get; private set; }

    /// <summary>The class of the application's document window.</summary>
    internal static string WindowClass(OfficeApp kind) => kind switch
    {
        OfficeApp.Word => "OpusApp",
        OfficeApp.Excel => "XLMAIN",
        _ => "PPTFrameClass",
    };

    public static bool IsInstalled(OfficeApp kind) => Type.GetTypeFromProgID(ProgId(kind)) is not null;

    /// <summary>Opens <paramref name="path"/>, or creates and saves it first. Throws when Office cannot.</summary>
    public static async Task<OfficeSession> OpenAsync(OfficeApp kind, string path, bool readOnly, bool create)
    {
        var started = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            started.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Office automation",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var dispatcher = await started.Task.ConfigureAwait(true);
        var session = new OfficeSession(kind, dispatcher);
        try
        {
            await dispatcher.InvokeAsync(() => session.Open(path, readOnly, create)).Task.ConfigureAwait(true);
            return session;
        }
        catch
        {
            await session.CloseAsync().ConfigureAwait(true);
            throw;
        }
    }

    /// <summary>
    /// Excel, once docked: it now and then shows an empty ribbon and no status bar after starting behind another window.
    /// Activating the workbook and switching the status bar makes it lay its frame out again.
    /// </summary>
    public Task SettleAsync() => _dispatcher.InvokeAsync(() =>
    {
        if (_kind == OfficeApp.Excel)
        {
            _document!.Activate();
            _app!.DisplayStatusBar = false;
            _app!.DisplayStatusBar = true;
        }
    }).Task;

    /// <summary>False once the document is closed, from inside Office too. A busy Office counts as open.</summary>
    public Task<bool> IsOpenAsync() => _dispatcher.InvokeAsync(() =>
    {
        try
        {
            _ = (string)_document!.Name;
            return true;
        }
        catch (COMException ex) when (ex.HResult is CallRejected or ServerBusy)
        {
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }).Task;

    /// <summary>Unsaved changes. Throws <see cref="COMException"/> while Office is busy, for example in a dialog.</summary>
    public Task<bool> IsModifiedAsync() => _dispatcher.InvokeAsync(() =>
    {
        object saved = _document!.Saved;
        return saved is not (true or -1);
    }).Task;

    public Task SaveAsync() => _dispatcher.InvokeAsync(() => { _document!.Save(); }).Task;

    /// <summary>Closes the document without saving, and Office when nothing else is open in it, then ends the thread.</summary>
    public async Task CloseAsync()
    {
        if (_dispatcher.HasShutdownStarted)
        {
            return;
        }

        await _dispatcher.InvokeAsync(Close).Task.ConfigureAwait(false);
        _dispatcher.InvokeShutdown();
    }

    private static string ProgId(OfficeApp kind) => kind switch
    {
        OfficeApp.Word => "Word.Application",
        OfficeApp.Excel => "Excel.Application",
        _ => "PowerPoint.Application",
    };

    private void Open(string path, bool readOnly, bool create)
    {
        var type = Type.GetTypeFromProgID(ProgId(_kind)) ?? throw new InvalidOperationException($"{OfficeFiles.Name(_kind)} is not installed.");
        _app = Activator.CreateInstance(type) ?? throw new InvalidOperationException($"{OfficeFiles.Name(_kind)} did not start.");
        switch (_kind)
        {
            case OfficeApp.Word:
                _app.Visible = true; // started hidden, Word and Excel build no ribbon; OfficeWindowHider covers them
                if (create)
                {
                    _document = _app.Documents.Add();
                    _document.SaveAs2(path, 16); // wdFormatDocumentDefault
                }
                else
                {
                    _document = _app.Documents.Open(FileName: path, ReadOnly: readOnly, AddToRecentFiles: false);
                }

                Window = new IntPtr((int)_document.ActiveWindow.Hwnd);
                break;

            case OfficeApp.Excel:
                _app.Visible = true;
                if (create)
                {
                    _document = _app.Workbooks.Add();
                    _document.SaveAs(path, 51); // xlOpenXMLWorkbook
                }
                else
                {
                    _document = _app.Workbooks.Open(Filename: path, ReadOnly: readOnly, AddToMru: false);
                }

                Window = new IntPtr((int)_document.Windows[1].Hwnd);
                break;

            default:
                if (create)
                {
                    _document = _app.Presentations.Add(-1); // msoTrue: with a window
                    _document.SaveAs(path, 24); // ppSaveAsOpenXMLPresentation
                }
                else
                {
                    _document = _app.Presentations.Open(FileName: path, ReadOnly: readOnly ? -1 : 0, Untitled: 0, WithWindow: -1);
                }

                Window = FindPowerPointWindow((string)_document.Name);
                break;
        }

        if (readOnly)
        {
            Quietly(LockAgainstEditing);
        }
    }

    /// <summary>
    /// Office's read-only mode only stops saving over the file; this stops typing too, in Word and Excel. In memory only:
    /// nothing is saved, and the document is closed without saving. PowerPoint has no such switch: marking a read-only
    /// presentation as final blocks in a dialog, so it can be changed on screen but never saved over the file.
    /// </summary>
    private void LockAgainstEditing()
    {
        switch (_kind)
        {
            case OfficeApp.Word:
                _document!.Protect(3); // wdAllowOnlyReading
                _app!.TaskPanes[6].Visible = false; // wdTaskPaneDocumentProtection: its "stop protection" is no use here
                break;
            case OfficeApp.Excel:
                foreach (var sheet in _document!.Worksheets)
                {
                    sheet.Protect();
                }

                break;
        }
    }

    private void Close()
    {
        Quietly(() =>
        {
            if (_document is null)
            {
                return;
            }

            // Saving was the page's question; each application asks differently otherwise.
            switch (_kind)
            {
                case OfficeApp.Word:
                    _document.Close(0); // wdDoNotSaveChanges
                    break;
                case OfficeApp.Excel:
                    _document.Close(false);
                    break;
                default:
                    _document.Close();
                    break;
            }
        });

        // PowerPoint runs one instance per user: documents opened by the user keep it, and their windows, alive.
        Quietly(() =>
        {
            if (_app is null)
            {
                return;
            }

            int open = _kind switch
            {
                OfficeApp.Word => _app.Documents.Count,
                OfficeApp.Excel => _app.Workbooks.Count,
                _ => _app.Presentations.Count,
            };
            if (open == 0)
            {
                _app.Quit();
            }
        });

        foreach (var com in new object?[] { _document, _app })
        {
            if (com is not null && Marshal.IsComObject(com))
            {
                Marshal.FinalReleaseComObject(com);
            }
        }

        _document = null;
        _app = null;

        // Each collection touched on the way (Workbooks, Windows...) holds Office too: Excel stays alive until they go.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    /// <summary>
    /// PowerPoint's object model has no window handle: its frame is found by class and by the title, which starts with the
    /// presentation's name. A moment may pass before the window appears.
    /// </summary>
    private static IntPtr FindPowerPointWindow(string name)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var found = IntPtr.Zero;
            EnumWindows((window, _) =>
            {
                var text = new char[512];
                if (new string(text, 0, GetClassName(window, text, text.Length)) == WindowClass(OfficeApp.PowerPoint) &&
                    new string(text, 0, GetWindowText(window, text, text.Length)).StartsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    found = window;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero)
            {
                return found;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException($"The PowerPoint window of '{name}' was not found.");
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, [Out] char[] name, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, [Out] char[] text, int capacity);

    /// <summary>The user may have closed the document or Office already: then there is nothing left to do.</summary>
    private static void Quietly(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MainWindow.Trace($"office: {ex.Message}");
        }
    }
}
