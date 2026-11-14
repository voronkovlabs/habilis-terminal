using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using HabilisTerminal.App.Hosting;
using HabilisTerminal.Shell.Integration;

namespace HabilisTerminal.App;

/// <summary>
/// The console that grows over both panels (issue #82, experiment). While a command runs and its output no longer fits
/// the console, the console's top edge rises line by line over the panels, up to the top of the window; a full-screen
/// program (alternate screen) gets the whole window at once. Afterwards it stays until Esc at the prompt (which only
/// collapses it) or until the focus moves to a panel.
/// </summary>
public partial class MainWindow
{
    private bool OverflowExpanded => Workspace.OverlayRows > 0;

    private void WireOverflow(ConsoleTab tab)
    {
        var host = tab.Host;
        void Started()
        {
            tab.OverflowPosted = (0, false);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // A new command grows from where the console is now; focus moving away stops it only for one command.
                tab.OverflowBase = tab == _tab ? Workspace.OverlayRows : tab.OverlayRows;
                tab.OverflowSuppressed = false;
            });
        }

        // PowerShell tells where the output starts (command-start); zsh and bash cannot, so the emulator's cursor at the
        // command's OSC 133;C is taken instead (issue #312).
        var markedByEmulator = host.Kind != ShellKind.Pwsh;
        host.CommandOutputStarted += (row, rows, columns) =>
        {
            lock (tab.OverflowLock)
            {
                tab.Overflow.CommandStarted(row, rows, columns);
                Started();
            }
        };
        host.Output += text =>
        {
            (int Rows, bool Alternate) now;
            lock (tab.OverflowLock)
            {
                // The emulator was fed this text first (ShellConnection subscribed before this).
                if (markedByEmulator && tab.Connection.Emulator.TakeCommandMark() is { } mark)
                {
                    tab.Overflow.CommandStartedAtMark(mark.Row, mark.Rows, mark.Columns);
                    Started();
                }

                tab.Overflow.Feed(text);
                now = (tab.Overflow.OverflowRows, tab.Overflow.AlternateScreen);
                if (now == tab.OverflowPosted)
                {
                    return;
                }

                tab.OverflowPosted = now;
            }

            // A console not in front never grows over the panels; ShowTab catches up.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (tab == _tab)
                {
                    GrowConsole(now.Rows, now.Alternate);
                    UpdateHintButton();
                }
            });
        };
        host.StateChanged += state =>
        {
            if (state == ShellState.Idle)
            {
                lock (tab.OverflowLock)
                {
                    tab.Overflow.CommandFinished();
                }
            }
        };
    }

    /// <summary>Only grows during a command: the console never shrinks under the user's eyes.</summary>
    private void GrowConsole(int overflowRows, bool alternateScreen)
    {
        if (_tab is not { } tab || tab.OverflowSuppressed || IsPageOpen || !(Workspace.LeftVisible && Workspace.RightVisible))
        {
            return;
        }

        var max = Workspace.MaxOverlayRows;
        var target = alternateScreen ? max : Math.Min(max, tab.OverflowBase + overflowRows);
        if (target > Workspace.OverlayRows)
        {
            Workspace.OverlayRows = target;
        }
    }

    /// <summary>Back to the console's own height; <paramref name="stopGrowing"/> keeps it there for the running command.</summary>
    private void CollapseConsole(bool stopGrowing)
    {
        if (_tab is { } tab)
        {
            if (stopGrowing && tab.Host.State == ShellState.Busy)
            {
                tab.OverflowSuppressed = true;
            }

            tab.OverflowBase = 0;
        }

        if (OverflowExpanded)
        {
            Workspace.OverlayRows = 0;
        }
    }

    /// <summary>Esc at the prompt with the console expanded: collapse it, and the shell does not see the key.</summary>
    private bool CollapseConsoleOnEscape()
    {
        if (_tab is not { } tab)
        {
            return false;
        }

        bool alternate;
        lock (tab.OverflowLock)
        {
            alternate = tab.Overflow.AlternateScreen;
        }

        if (!OverflowExpanded || !ConsoleHasKeys || alternate || _shell?.Host.State != ShellState.Idle)
        {
            return false;
        }

        CollapseConsole(stopGrowing: false);
        return true;
    }
}
