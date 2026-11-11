using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HabilisTerminal.Core.Reporting;
using HabilisTerminal.Core.Settings;

namespace HabilisTerminal.App.Reporting;

/// <summary>
/// Catches what nothing else caught (UI thread, other threads, unobserved tasks), writes a crash report next to the state
/// and offers it on the next start. A UI or thread crash ends the app; an unobserved task does not, its report just waits.
/// </summary>
public static class CrashCapture
{
    private const int TraceLines = 40;
    private static int _written;

    public static void Install(Application app)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Write(ex, "appdomain");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception, "task", always: true);
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Write(e.Exception, "dispatcher");
            e.Handled = true;
            Environment.Exit(1);
        };
    }

    /// <summary>
    /// Development trigger: <c>HABI_TEST_CRASH=ui|thread|task</c> crashes the app a few seconds after the window opens,
    /// to check the report and the next-start dialog in a test copy.
    /// </summary>
    public static void StartTestTrigger(Dispatcher dispatcher)
    {
        var kind = Environment.GetEnvironmentVariable("HABI_TEST_CRASH");
        if (string.IsNullOrEmpty(kind))
        {
            return;
        }

        var timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (sender, args) =>
        {
            ((DispatcherTimer)sender!).Stop();
            switch (kind)
            {
                case "thread":
                    new Thread(() => throw new InvalidOperationException("Test crash on a background thread (HABI_TEST_CRASH)")).Start();
                    break;
                case "task":
                    _ = Task.Run(() => throw new InvalidOperationException("Test unobserved task exception (HABI_TEST_CRASH)"));
                    Task.Delay(500).ContinueWith(_ =>
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }, TaskScheduler.Default);
                    break;
                default:
                    throw new InvalidOperationException($"Test crash on the UI thread (HABI_TEST_CRASH) in {Environment.CurrentDirectory}");
            }
        });
        timer.Start();
    }

    /// <summary>Where reports go: <see cref="App.Paths"/>, or resolved here when the crash came before startup did.</summary>
    public static string Directory => (App.Paths ?? AppPaths.Resolve(AppContext.BaseDirectory)).CrashDirectory;

    private static void Write(Exception exception, string source, bool always = false)
    {
        // The UI handler exits, which can also raise the AppDomain one: one report per crash.
        if (!always && Interlocked.Exchange(ref _written, 1) == 1)
        {
            return;
        }

        try
        {
            CrashReport.FromException(exception, source, ReportSender.Version, ReportEnvironment.Windows(), ReadTrace()).Save(Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write: nothing more can be done while crashing.
        }
    }

    private static string[] ReadTrace()
    {
        var file = Environment.GetEnvironmentVariable("HABI_TRACE");
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > TraceLines)
                {
                    lines.Dequeue();
                }
            }

            return [.. lines];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
