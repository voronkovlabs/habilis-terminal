using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Interactivity;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Operations;

namespace HabilisTerminal.App.Operations;

/// <summary>Answers operation questions with dialogs on the UI thread (the worker waits) and feeds the progress card.</summary>
internal sealed class UiOperationHandler(Window owner, OperationToasts.Card card) : IOperationHandler
{
    public ConflictDecision ResolveConflict(ConflictInfo conflict) =>
        Avalonia.Threading.Dispatcher.UIThread.Invoke(() => OperationDialogs.Conflict(owner, conflict));

    public ErrorAction ResolveError(OperationError failure) =>
        Avalonia.Threading.Dispatcher.UIThread.Invoke(() => OperationDialogs.Error(owner, failure));

    public ConfirmationAnswer Confirm(Confirmation confirmation) =>
        Avalonia.Threading.Dispatcher.UIThread.Invoke(() => OperationDialogs.Confirm(owner, confirmation));

    public void ReportProgress(OperationProgress progress) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => card.Update(progress));
}
