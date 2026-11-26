using HabilisTerminal.App.Hosting;
using HabilisTerminal.App.Panels;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Drives;

namespace HabilisTerminal.App;

/// <summary>
/// Eject (issue #407): the ⏏ in a panel's header, the ⏏ and Del in the drive menu, Ctrl+Shift+E. Panels and consoles
/// leave the drive first, since an open folder keeps Windows from letting it go.
/// </summary>
public partial class MainWindow
{
    private int _ejecting;

    /// <summary>Ctrl+Shift+E and the header's ⏏: the drive the panel stands on.</summary>
    private void EjectPanelDrive(FilePanel panel)
    {
        if (!panel.IsPlugin && DriveEject.RootOf(panel.CurrentPath) is { } root)
        {
            Fire(EjectDriveAsync(root, panel));
        }
    }

    /// <summary>Takes everything off the drive, asks Windows to eject it and waits until it has gone.</summary>
    /// <param name="report">Where a failure is told; the active panel when null.</param>
    /// <param name="item">The drive menu's row, whose right side tells how it went.</param>
    private async Task EjectDriveAsync(string root, FilePanel? report = null, QuickPickItem? item = null)
    {
        if (Interlocked.Exchange(ref _ejecting, 1) == 1)
        {
            return;
        }

        var name = root.TrimEnd('\\', '/');
        try
        {
            var type = await Task.Run(() => new DriveInfo(root).DriveType);
            if (!DriveEject.CanEject(type))
            {
                (report ?? _active).Message = $"{name} is not a removable drive: Windows cannot eject it.";
                return;
            }

            if (item is not null)
            {
                item.Detail = "ejecting…";
            }

            await LeaveDriveAsync(root);
            string? error = null;
            using (new WaitCard(Toasts, $"Ejecting {name}"))
            {
                try
                {
                    await RunOnStaAsync(() => ShellVerbs.Eject(root));
                    if (!await DriveEject.WaitGoneAsync(() => DriveEject.IsGone(root), DriveEject.GoneWithin))
                    {
                        error = $"Windows did not eject {name}: a program may be using it. Close what is open there and try again.";
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException)
                {
                    error = $"Ejecting {name} failed: {ex.Message}";
                }
            }

            if (item is not null)
            {
                item.Detail = error is null ? "ejected · safe to remove" : "not ejected";
            }

            if (error is not null)
            {
                (report ?? _active).Message = error;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _ejecting, 0);
        }
    }

    /// <summary>Panels on the drive, in every console tab, go home; idle consoles there follow.</summary>
    private async Task LeaveDriveAsync(string root)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var panels = _tabs.SelectMany(t => new[] { t.Left, t.Right }).Append(_left).Append(_right).Where(p => p is not null).Distinct();
        foreach (var panel in panels.Where(p => !p.IsPlugin && DriveEject.IsOn(p.CurrentPath, root)).ToList())
        {
            await panel.NavigateAsync(home);
        }

        SyncShellDirectory();
        foreach (var tab in _tabs.Where(t => !t.CommandRunning && DriveEject.IsOn(t.Host.CurrentDirectory, root)))
        {
            tab.Host.ChangeDirectory(home);
        }

        // cd reaches PowerShell a moment later; its handle on the folder would keep the drive.
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < until && _tabs.Any(t => !t.CommandRunning && DriveEject.IsOn(t.Host.CurrentDirectory, root)))
        {
            await Task.Delay(50);
        }
    }

    /// <summary>Shell automation objects live in a single-threaded apartment; the UI thread must not wait on them.</summary>
    private static Task RunOnStaAsync(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        { IsBackground = true, Name = "Eject" };
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
