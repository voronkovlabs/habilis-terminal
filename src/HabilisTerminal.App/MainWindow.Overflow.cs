using System.Windows;
using HabilisTerminal.Core.Panels;
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
    private readonly TerminalOverflow _overflow = new();
    private readonly Lock _overflowLock = new();
    private (int Rows, bool Alternate) _overflowPosted;
    private int _overflowBase;
    private bool _overflowSuppressed;

    private bool OverflowExpanded => Workspace.OverlayRows > 0;

    private void WireOverflow(ShellHost host)
    {
        host.CommandOutputStarted += (row, rows, columns) =>
        {
            lock (_overflowLock)
            {
                _overflow.CommandStarted(row, rows, columns);
                _overflowPosted = (0, false);
            }

            Dispatcher.BeginInvoke(() =>
            {
                // A new command grows from where the console is now; focus moving away stops it only for one command.
                _overflowBase = Workspace.OverlayRows;
                _overflowSuppressed = false;
            });
        };
        host.Output += text =>
        {
            (int Rows, bool Alternate) now;
            lock (_overflowLock)
            {
                _overflow.Feed(text);
                now = (_overflow.OverflowRows, _overflow.AlternateScreen);
                if (now == _overflowPosted)
                {
                    return;
                }

                _overflowPosted = now;
            }

            Dispatcher.BeginInvoke(() => GrowConsole(now.Rows, now.Alternate));
        };
        host.StateChanged += state =>
        {
            if (state == ShellState.Idle)
            {
                lock (_overflowLock)
                {
                    _overflow.CommandFinished();
                }
            }
        };
    }

    /// <summary>Only grows during a command: the console never shrinks under the user's eyes.</summary>
    private void GrowConsole(int overflowRows, bool alternateScreen)
    {
        if (_overflowSuppressed || IsPageOpen || !(Workspace.LeftVisible && Workspace.RightVisible))
        {
            return;
        }

        var max = Workspace.MaxOverlayRows;
        var target = alternateScreen ? max : Math.Min(max, _overflowBase + overflowRows);
        if (target > Workspace.OverlayRows)
        {
            Workspace.OverlayRows = target;
        }
    }

    /// <summary>Back to the console's own height; <paramref name="stopGrowing"/> keeps it there for the running command.</summary>
    private void CollapseConsole(bool stopGrowing)
    {
        if (stopGrowing && _shell?.Host.State == ShellState.Busy)
        {
            _overflowSuppressed = true;
        }

        _overflowBase = 0;
        if (OverflowExpanded)
        {
            Workspace.OverlayRows = 0;
        }
    }

    /// <summary>Esc at the prompt with the console expanded: collapse it, and the shell does not see the key.</summary>
    private bool CollapseConsoleOnEscape()
    {
        bool alternate;
        lock (_overflowLock)
        {
            alternate = _overflow.AlternateScreen;
        }

        if (!OverflowExpanded || !TerminalHasFocus || alternate || _shell?.Host.State != ShellState.Idle)
        {
            return false;
        }

        CollapseConsole(stopGrowing: false);
        return true;
    }
}
