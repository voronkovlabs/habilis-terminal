using System.Windows;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Operations;

namespace HabilisTerminal.App.Operations;

/// <summary>Answers operation questions with dialogs on the UI thread (the worker waits) and feeds the progress card.</summary>
internal sealed class UiOperationHandler(Window owner, OperationToasts.Card card) : IOperationHandler
{
    public ConflictDecision ResolveConflict(ConflictInfo conflict) =>
        owner.Dispatcher.Invoke(() => OperationDialogs.Conflict(owner, conflict));

    public ErrorAction ResolveError(OperationError failure) =>
        owner.Dispatcher.Invoke(() => OperationDialogs.Error(owner, failure));

    public ConfirmationAnswer Confirm(Confirmation confirmation) =>
        owner.Dispatcher.Invoke(() => OperationDialogs.Confirm(owner, confirmation));

    public void ReportProgress(OperationProgress progress) =>
        owner.Dispatcher.BeginInvoke(() => card.Update(progress));
}
